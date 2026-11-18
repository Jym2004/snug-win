using System;
using System.IO;
using SnugWin.Core.Persistence;
using SnugWin.Core.PhoneScreen;
using SnugWin.Core.Session;
using Xunit;

namespace SnugWin.Core.Tests;

public sealed class SessionAndDisplaySafetyTests
{
    [Fact]
    public void OwnedDisplayStore_TracksAndReleasesOnlySnugOwnedIds()
    {
        string dir = Path.Combine(Path.GetTempPath(), "snug-owned-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var store = new OwnedDisplayStore(Path.Combine(dir, "owned-displays.json"));
            Assert.Empty(store.LoadIds());

            store.RecordOwned(0);
            store.RecordOwned(0); // idempotent
            store.RecordOwned(2);
            Assert.Equal(new uint[] { 0, 2 }, store.LoadIds());

            store.ReleaseOwned(0);
            Assert.Equal(new uint[] { 2 }, store.LoadIds());

            store.ReleaseOwned(2);
            Assert.Empty(store.LoadIds());
            Assert.False(File.Exists(Path.Combine(dir, "owned-displays.json")));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void StreamLogActivityTracker_RequiresContinuousGrowthAndExpiresCleanly()
    {
        var tracker = new StreamLogActivityTracker();
        var t0 = DateTimeOffset.UnixEpoch;

        // No process -> definitely inactive
        Assert.Equal(false, tracker.Observe(null, null, false, t0));

        // Process running but no readable log -> unknown (null), not false positive
        Assert.Null(tracker.Observe(1234, null, false, t0));

        // Baseline read, then single isolated write -> still false
        Assert.Equal(false, tracker.Observe(1234, 1000, true, t0));
        Assert.Equal(false, tracker.Observe(1234, 1200, true, t0.AddSeconds(0.5)));

        // Second consecutive write within 3s -> active streaming
        Assert.Equal(true, tracker.Observe(1234, 1500, true, t0.AddSeconds(1.2)));

        // Still within 2s hold without new bytes -> active
        Assert.Equal(true, tracker.Observe(1234, 1500, true, t0.AddSeconds(2.5)));

        // Past 2s hold with no further growth -> stops promptly (false)
        Assert.Equal(false, tracker.Observe(1234, 1500, true, t0.AddSeconds(3.2)));

        // PID switch resets prior active history
        Assert.Equal(false, tracker.Observe(5678, 100, true, t0.AddSeconds(3.5)));
    }

    [Fact]
    public void UUSignals_DisconnectDebouncesCleanlyWhenStreamLogStops()
    {
        var session = new RemoteSession();
        var tracker = new StreamLogActivityTracker();
        var t0 = DateTimeOffset.UnixEpoch;

        // Connect via overlay/UIA, stream keeps growing
        var trans1 = session.Update(new UUSignals(true, ControllerTable.FromNames("iPhone"), tracker.Observe(100, 500, true, t0)), t0);
        Assert.Single(trans1);
        Assert.IsType<SessionTransition.Began>(trans1[0]);

        tracker.Observe(100, 700, true, t0.AddSeconds(0.5));
        tracker.Observe(100, 900, true, t0.AddSeconds(1.0));
        // Overlay & UIA momentarily unreadable, active stream keeps session connected
        Assert.Empty(session.Update(new UUSignals(false, ControllerTable.Unreadable, tracker.Observe(100, 900, true, t0.AddSeconds(2.0))), t0.AddSeconds(2.0)));
        Assert.True(session.IsConnected);

        // Stream stops -> CaptureActive becomes false -> disconnects after 1.5s debounce
        Assert.Empty(session.Update(new UUSignals(false, ControllerTable.Unrecognized, tracker.Observe(100, 900, true, t0.AddSeconds(3.5))), t0.AddSeconds(3.5)));
        var transEnd = session.Update(new UUSignals(false, ControllerTable.Unrecognized, tracker.Observe(100, 900, true, t0.AddSeconds(5.2))), t0.AddSeconds(5.2));
        Assert.Single(transEnd);
        Assert.IsType<SessionTransition.Interrupted>(transEnd[0]);
        Assert.False(session.IsConnected);
    }
}
