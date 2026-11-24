using System;
using System.Linq;
using SnugWin.Core.Session;
using Xunit;

namespace SnugWin.Core.Tests;

public sealed class RustDeskConnectionParserTests
{
    [Fact]
    public void ConnectedRemoteCard_IsEligible()
    {
        var tree = Card(
            RustDeskAccessibleNode.Text("Generic peer"),
            RustDeskAccessibleNode.Text("Connected"),
            RustDeskAccessibleNode.Text("Disconnect"));

        Assert.True(RustDeskConnectionParser.HasAuthorizedDesktopConnection([tree]));
    }

    [Fact]
    public void RequestAccessAndDisconnectedCards_AreNotEligible()
    {
        var pending = Card(RustDeskAccessibleNode.Text("Request access to your device"), RustDeskAccessibleNode.Text("Accept"));
        var disconnected = Card(RustDeskAccessibleNode.Text("Disconnected"), RustDeskAccessibleNode.Text("Disconnect"));

        Assert.False(RustDeskConnectionParser.HasAuthorizedDesktopConnection([pending]));
        Assert.False(RustDeskConnectionParser.HasAuthorizedDesktopConnection([disconnected]));
    }

    [Fact]
    public void NonDesktopCards_AreNotEligible()
    {
        foreach (string type in new[] { "Transfer file", "View camera", "Terminal", "Port Forward" })
        {
            var card = Card(RustDeskAccessibleNode.Text("Connected"), RustDeskAccessibleNode.Text("Disconnect"),
                RustDeskAccessibleNode.Text(type));
            Assert.False(RustDeskConnectionParser.HasAuthorizedDesktopConnection([card]));
        }
    }

    [Fact]
    public void MixedCards_KeepEligibleRemoteCardIndependent()
    {
        var file = Card(RustDeskAccessibleNode.Text("Connected"), RustDeskAccessibleNode.Text("Disconnect"),
            RustDeskAccessibleNode.Text("Transfer file"));
        var remote = Card(RustDeskAccessibleNode.Text("Connected"), RustDeskAccessibleNode.Text("Disconnect"));

        Assert.True(RustDeskConnectionParser.HasAuthorizedDesktopConnection([file, remote]));
    }

    [Fact]
    public void PresenceTrackerRequiresTwoSamplesAndExpiresStaleState()
    {
        var tracker = new RustDeskPresenceTracker();
        var t0 = DateTimeOffset.UtcNow;

        Assert.False(tracker.Observe(true, t0));
        Assert.True(tracker.Observe(true, t0.AddSeconds(1)));
        Assert.True(tracker.Observe(null, t0.AddSeconds(2)));
        Assert.False(tracker.Observe(null, t0.AddSeconds(7.1)));

        Assert.False(tracker.Observe(false, t0.AddSeconds(8)));
        Assert.False(tracker.Observe(false, t0.AddSeconds(9)));
    }

    [Fact]
    public void PresenceTrackerRequiresTwoNegativeSamplesToDisconnect()
    {
        var tracker = new RustDeskPresenceTracker();
        var t0 = DateTimeOffset.UtcNow;
        tracker.Observe(true, t0);
        tracker.Observe(true, t0.AddSeconds(1));

        Assert.True(tracker.Observe(false, t0.AddSeconds(2)));
        Assert.False(tracker.Observe(false, t0.AddSeconds(3)));
    }

    private static RustDeskAccessibleNode Card(params RustDeskAccessibleNode[] children)
    {
        var status = children.First(child => IsStatus(child.Name));
        var header = RustDeskAccessibleNode.Group(children.Where(child => !ReferenceEquals(child, status)).ToArray());
        var statusNode = new RustDeskAccessibleNode(
            status.Name,
            children.Where(child => !ReferenceEquals(child, status)).ToArray(),
            Role: 20);
        return RustDeskAccessibleNode.Group(header, statusNode);
    }

    private static bool IsStatus(string? name) => name is not null
        && (name.Equals("Connected", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Disconnected", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Request access to your device", StringComparison.OrdinalIgnoreCase)
            || name.Equals("已连接", StringComparison.Ordinal)
            || name.Equals("已断开", StringComparison.Ordinal)
            || name.Equals("请求访问", StringComparison.Ordinal));
}
