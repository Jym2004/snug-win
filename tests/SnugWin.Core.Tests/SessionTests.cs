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
    public void ConnectionTracker_DropsStalePhoneWhenReadableControllerBecomesDesktop()
    {
        var tracker = new ConnectionTracker();
        var t0 = DateTimeOffset.UtcNow;
        tracker.Update(new UUSignals(true, ControllerTable.FromNames("Phone A"), true), t0);

        // Unreadable sample must not erase what we already know.
        Assert.Null(tracker.Update(new UUSignals(true, ControllerTable.Unreadable, true), t0.AddSeconds(1)));
        Assert.Equal(new[] { "Phone A" }, tracker.Controllers);

        var change = tracker.Update(
            new UUSignals(true, ControllerTable.FromNames("Office-Laptop"), true), t0.AddSeconds(2));

        Assert.IsType<ConnectionEvent.ControllersChanged>(change);
        Assert.False(ControllerClassifier.WantsPhoneScreen(tracker.Controllers));
    }

    [Fact]
    public void ControllerTableParser_ReadsQtControlledPanel()
    {
        var panel = UuElement.ControlledPanelNode([
            UuElement.TextNode("本机被控中"),
            UuElement.RowNode([
                UuElement.TextNode("Sample-Book"),
                UuElement.TextNode("远程控制"),
                UuElement.TextNode("连接时长 00:15")
            ])
        ]);

        var parsed = ControllerTableParser.ParseWindows([panel]);

        Assert.Equal(["Sample-Book"], parsed.Names);
        Assert.Equal(ControllerKind.Desktop, ControllerClassifier.Classify(parsed.Names[0]));
    }

    [Fact]
    public void ControllerClassifier_TreatsLaptopBrandNamesAndThinkPadAsDesktop()
    {
        Assert.Equal(ControllerKind.Desktop, ControllerClassifier.Classify("Sample-Book 16"));
        Assert.Equal(ControllerKind.Desktop, ControllerClassifier.Classify("My MacBook Pro"));
        Assert.Equal(ControllerKind.Desktop, ControllerClassifier.Classify("ThinkPad Windows"));
        Assert.Equal(ControllerKind.Tablet, ControllerClassifier.Classify("iPad Air"));
        Assert.Equal(ControllerKind.Phone, ControllerClassifier.Classify("Pixel 9"));
    }

    [Fact]
    public void ManualOverride_MarksPresentWithoutInjectingFakePhoneController()
    {
        var signals = new UUSignals(false, ControllerTable.Unreadable, null) { ManualOverride = true };
        Assert.True(signals.IsPresent);
        Assert.Empty(signals.Controllers.Names);
        Assert.Contains("手动开启", signals.ToString());
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
    }
}
