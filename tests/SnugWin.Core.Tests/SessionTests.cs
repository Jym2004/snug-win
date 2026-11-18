using System;
using SnugWin.Core.Session;
using Xunit;

namespace SnugWin.Core.Tests;

public sealed class SessionTests
{
    [Fact]
    public void ConnectionTracker_DebouncesDisconnectAndRespectsQuietWindow()
    {
        var tracker = new ConnectionTracker();
        var t0 = DateTimeOffset.UtcNow;

        var ev1 = tracker.Update(new UUSignals(true, ControllerTable.Unreadable, null), t0);
        Assert.IsType<ConnectionEvent.Connected>(ev1);
        Assert.True(tracker.IsConnected);

        // Brief absence < 1.5s should not disconnect
        Assert.Null(tracker.Update(new UUSignals(false, ControllerTable.Unreadable, false), t0.AddSeconds(1.0)));
        Assert.True(tracker.IsConnected);

        // Quiet window prevents disconnect for 5s
        tracker.NoteSelfInducedChange(t0.AddSeconds(1.0));
        Assert.Null(tracker.Update(new UUSignals(false, ControllerTable.Unreadable, false), t0.AddSeconds(4.0)));
        Assert.True(tracker.IsConnected);

        // After quiet window + debounce, disconnects
        var ev2 = tracker.Update(new UUSignals(false, ControllerTable.Unreadable, false), t0.AddSeconds(7.0));
        Assert.IsType<ConnectionEvent.Disconnected>(ev2);
        Assert.False(tracker.IsConnected);
    }

    [Fact]
    public void RemoteSession_PreservesEpisodeAcrossBriefDisconnect()
    {
        var session = new RemoteSession();
        var t0 = DateTimeOffset.UtcNow;

        var b = session.Update(new UUSignals(true, ControllerTable.FromNames("iPhone 17"), true), t0);
        Assert.IsType<SessionTransition.Began>(Assert.Single(b));
        Assert.True(session.InEpisode);

        var d = session.Update(new UUSignals(false, ControllerTable.Unreadable, false), t0.AddSeconds(2));
        Assert.IsType<SessionTransition.Interrupted>(Assert.Single(d));
        Assert.True(session.InEpisode);

        // Reconnect within 15s -> Resumed
        var r = session.Update(new UUSignals(true, ControllerTable.FromNames("iPhone 17"), true), t0.AddSeconds(10));
        Assert.IsType<SessionTransition.Resumed>(Assert.Single(r));
        Assert.True(session.InEpisode);

        // Disconnect and pass 15s -> Ended
        session.Update(new UUSignals(false, ControllerTable.Unreadable, false), t0.AddSeconds(12));
        var end = session.Update(new UUSignals(false, ControllerTable.Unreadable, false), t0.AddSeconds(30));
        Assert.IsType<SessionTransition.Ended>(Assert.Single(end));
        Assert.False(session.InEpisode);
    }

    [Fact]
    public void ConnectionTracker_DropsStalePhoneWhenReadableControllerChangesToDesktop()
    {
        var tracker = new ConnectionTracker();
        var t0 = DateTimeOffset.UtcNow;
        tracker.Update(new UUSignals(true, ControllerTable.FromNames("iPhone 17"), true), t0);

        Assert.Null(tracker.Update(new UUSignals(true, ControllerTable.Unreadable, true), t0.AddSeconds(1)));
        Assert.Equal(new[] { "iPhone 17" }, tracker.Controllers);

        var change = tracker.Update(
            new UUSignals(true, ControllerTable.FromNames("Office-Laptop"), true), t0.AddSeconds(2));

        Assert.IsType<ConnectionEvent.ControllersChanged>(change);
        Assert.Equal(new[] { "Office-Laptop" }, tracker.Controllers);
        Assert.False(ControllerClassifier.WantsPhoneScreen(tracker.Controllers));
    }

    [Fact]
    public void ControllerClassifier_DistinguishesPhoneAndDesktop()
    {
        Assert.Equal(ControllerKind.Phone, ControllerClassifier.Classify("iPhone 16 Pro"));
        Assert.Equal(ControllerKind.Tablet, ControllerClassifier.Classify("iPad Air"));
        Assert.Equal(ControllerKind.Desktop, ControllerClassifier.Classify("DESKTOP-PC (Windows)"));

        Assert.False(ControllerClassifier.WantsPhoneScreen(["My MacBook Pro"]));
        Assert.True(ControllerClassifier.WantsPhoneScreen(["My MacBook Pro", "iPhone 17"]));
        Assert.True(ControllerClassifier.WantsPhoneScreen([]));
    }

    [Fact]
    public void ControllerTableParser_IgnoresSettingsRowsAndRequiresControlledTable()
    {
        var settingsTree = UuElement.OtherNode([
            UuElement.TextNode("设置"),
            UuElement.TableNode([
                UuElement.RowNode([UuElement.TextNode("隐藏至菜单栏")]),
                UuElement.RowNode([UuElement.TextNode("仅使用临时验证码")])
            ])
        ]);
        var unrecognized = ControllerTableParser.ParseWindows([settingsTree]);
        Assert.Empty(unrecognized.Names);
        Assert.True(unrecognized.Readable);

        var controlledTree = UuElement.OtherNode([
            UuElement.TextNode("本机被控中"),
            UuElement.TableNode([
                UuElement.RowNode([
                    UuElement.TextNode("iPhone 17 Pro"),
                    UuElement.TextNode("远程控制"),
                    UuElement.TextNode("连接时长 00:12")
                ]),
                UuElement.RowNode([
                    UuElement.TextNode("MacBook"),
                    UuElement.TextNode("远程协助")
                ])
            ])
        ]);
        var recognized = ControllerTableParser.ParseWindows([controlledTree]);
        Assert.Equal(["iPhone 17 Pro"], recognized.Names);
        Assert.True(recognized.Readable);

        var qtControlledPanel = UuElement.ControlledPanelNode([
            UuElement.TextNode("本机被控中"),
            UuElement.RowNode([
                UuElement.TextNode("Sample-Book"),
                UuElement.TextNode("远程控制"),
                UuElement.TextNode("连接时长 00:15")
            ])
        ]);
        var qtRecognized = ControllerTableParser.ParseWindows([qtControlledPanel]);
        Assert.Equal(["Sample-Book"], qtRecognized.Names);
        Assert.True(qtRecognized.Readable);
        Assert.Equal(ControllerKind.Desktop, ControllerClassifier.Classify(qtRecognized.Names[0]));
    }

    [Fact]
    public void ManualOverride_DoesNotPolluteControllerGroupAfterTurningOff()
    {
        var session = new RemoteSession();
        var t0 = DateTimeOffset.UtcNow;

        session.Update(
            new UUSignals(true, ControllerTable.FromNames("Office-Laptop"), true) { ManualOverride = true },
            t0);

        Assert.Equal(["Office-Laptop"], session.EpisodeControllers(t0));
        Assert.False(ControllerClassifier.WantsPhoneScreen(session.EpisodeControllers(t0)));
    }
}
