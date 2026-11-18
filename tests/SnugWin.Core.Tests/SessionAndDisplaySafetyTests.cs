using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
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

    [Fact]
    public async Task PhoneDesk_DismissScreenAndReenterScreen_RestoresAndRecreatesWithinEpisode()
    {
        string dir = Path.Combine(Path.GetTempPath(), "snug-desk-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var displays = new FakeDisplays();
            var windows = new FakeWindows();
            var apps = new FakeApps();
            var win = new WindowInfo(new WindowKey(101, 0xABCD), "Notepad", "notepad.exe", "Draft.txt");
            var originalRect = new RectD(120, 80, 900, 700);
            windows.Frames[win.Key] = originalRect;

            var desk = new PhoneDesk(displays, windows, apps, new SnugPaths(dir));
            desk.SetWanted(true);
            await desk.IdleAsync();

            Assert.Equal(PhoneScreenPhase.Ready, desk.State.ScreenPhase);
            Assert.Equal((uint)1, displays.CreateCount);
            uint? firstId = desk.State.DisplayId;
            Assert.NotNull(firstId);

            // Place window onto phone screen
            desk.Pull(win);
            await desk.IdleAsync();
            Assert.NotNull(desk.State.Placed);
            Assert.Equal(new RectD(2560, 0, 1080, 1800), windows.Frames[win.Key]);

            // User dismisses phone screen from phone home UI -> window restores & display retires, Dismissed=true
            desk.DismissScreen();
            await desk.IdleAsync();
            Assert.Equal(PhoneScreenPhase.Off, desk.State.ScreenPhase);
            Assert.True(desk.State.Dismissed);
            Assert.Equal((uint)1, displays.RetireCount);
            Assert.Equal(originalRect, windows.Frames[win.Key]);

            // Same UU session: user taps reenter button -> display recreated & Dismissed cleared
            desk.ReenterScreen();
            await desk.IdleAsync();
            Assert.False(desk.State.Dismissed);
            Assert.Equal(PhoneScreenPhase.Ready, desk.State.ScreenPhase);
            Assert.Equal((uint)2, displays.CreateCount);

            // Episode ends -> SetWanted(false) cleans up
            desk.SetWanted(false);
            await desk.IdleAsync();
            Assert.Equal(PhoneScreenPhase.Off, desk.State.ScreenPhase);
            Assert.False(desk.State.Dismissed);
            Assert.Equal((uint)2, displays.RetireCount);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void RemoteSession_EpisodeSurvivesTransientInterruptWithinReconnectWindow()
    {
        var session = new RemoteSession();
        var t0 = DateTimeOffset.UnixEpoch;

        var b = session.Update(new UUSignals(true, ControllerTable.FromNames("iPhone 16"), true), t0);
        Assert.IsType<SessionTransition.Began>(Assert.Single(b));
        Assert.True(session.InEpisode);

        // Wait past disconnect debounce (1.5s) -> Interrupted, but still InEpisode (15s reconnect window)
        session.Update(new UUSignals(false, ControllerTable.Unrecognized, false), t0.AddSeconds(1.0));
        var i = session.Update(new UUSignals(false, ControllerTable.Unrecognized, false), t0.AddSeconds(3.0));
        Assert.IsType<SessionTransition.Interrupted>(Assert.Single(i));
        Assert.False(session.IsConnected);
        Assert.True(session.InEpisode);

        // Reconnect within 15s -> Resumed
        var r = session.Update(new UUSignals(true, ControllerTable.FromNames("iPhone 16"), true), t0.AddSeconds(8.0));
        Assert.IsType<SessionTransition.Resumed>(Assert.Single(r));
        Assert.True(session.InEpisode);

        // Interrupt again: > 1.5s after t=8.0 (at t=10.0) emits Interrupted cleanly, then > 15s reconnect window (at t=26.0) emits Ended
        var i2 = session.Update(new UUSignals(false, ControllerTable.Unrecognized, false), t0.AddSeconds(10.0));
        Assert.IsType<SessionTransition.Interrupted>(Assert.Single(i2));
        var e = session.Update(new UUSignals(false, ControllerTable.Unrecognized, false), t0.AddSeconds(26.0));
        Assert.IsType<SessionTransition.Ended>(Assert.Single(e));
        Assert.False(session.InEpisode);
    }

    private sealed class FakeDisplays : IPhoneDisplays
    {
        private readonly HashSet<uint> _online = new();
        private readonly Dictionary<uint, ScreenFit> _fits = new();
        private uint _nextId;
        public uint CreateCount { get; private set; }
        public uint RetireCount { get; private set; }

        public Task<uint> CreateAsync(ScreenFit fit, CancellationToken ct = default)
        {
            uint id = _nextId++;
            _online.Add(id);
            _fits[id] = fit;
            CreateCount++;
            return Task.FromResult(id);
        }

        public Task<bool> ReshapeAsync(uint displayId, ScreenFit fit, CancellationToken ct = default)
        {
            _fits[displayId] = fit;
            return Task.FromResult(true);
        }

        public ScreenFit? FitOf(uint displayId) => _fits.TryGetValue(displayId, out var f) ? f : null;

        public Task<bool> RetireAsync(uint displayId, CancellationToken ct = default)
        {
            _online.Remove(displayId);
            RetireCount++;
            return Task.FromResult(true);
        }

        public bool IsOnline(uint displayId) => _online.Contains(displayId);
        public bool IsRemoteExclusiveDisplayActive() => false;
        public Task<bool> TryReactivateAsync(uint displayId, int width, CancellationToken ct = default) =>
            Task.FromResult(_online.Contains(displayId));
        public int? WidthOf(uint displayId) => _fits.TryGetValue(displayId, out var f) ? f.BaseWidth : null;
        public Task SetWidthAsync(uint displayId, int width, CancellationToken ct = default) => Task.CompletedTask;
        public RectD? BoundsOf(uint displayId) => _online.Contains(displayId) ? new RectD(2560, 0, 1080, 1920) : null;
        public RectD? WindowAreaOf(uint displayId) => _online.Contains(displayId) ? new RectD(2560, 0, 1080, 1800) : null;
        public RectD? MainArea() => new RectD(0, 0, 2560, 1600);
        public IReadOnlyList<OtherDisplay> OtherDisplays() => Array.Empty<OtherDisplay>();
        public Task SetModeAsync(uint displayId, DisplayModeInfo mode, bool permanently, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FakeWindows : IWindowControl
    {
        public Dictionary<WindowKey, RectD> Frames { get; } = new();

        public Task<bool> ExistsAsync(WindowKey window) => Task.FromResult(Frames.ContainsKey(window));
        public Task<bool> IsShownAsync(WindowKey window) => Task.FromResult(Frames.ContainsKey(window));
        public Task<WindowLook?> LookAsync(WindowKey window) =>
            Task.FromResult(Frames.TryGetValue(window, out var r) ? new WindowLook(r) : null);

        public Task<RectD?> MoveAsync(WindowKey window, RectD frame)
        {
            Frames[window] = frame;
            return Task.FromResult<RectD?>(frame);
        }

        public Task SetFullScreenAsync(WindowKey window, bool on) => Task.CompletedTask;
        public Task SetMinimizedAsync(WindowKey window, bool on) => Task.CompletedTask;
        public Task FocusAsync(WindowKey window) => Task.CompletedTask;
        public Task<bool> ZoomAsync(WindowKey window, int steps) => Task.FromResult(true);
        public Task<IReadOnlyList<WindowInfo>> ShownWindowsAsync(int pid) => Task.FromResult<IReadOnlyList<WindowInfo>>(Array.Empty<WindowInfo>());
        public Task<WindowInfo?> AskingWindowAsync(int pid) => Task.FromResult<WindowInfo?>(null);
        public Task<bool> CloseAsync(WindowKey window) => Task.FromResult(Frames.Remove(window));
        public Task<RectD?> CloseButtonAsync(WindowKey window) => Task.FromResult<RectD?>(null);
        public Task<Dictionary<WindowKey, RectD>> FramesAsync(int pid) => Task.FromResult(new Dictionary<WindowKey, RectD>());
        public Task<Dictionary<WindowKey, RectD>> SnapshotAsync() => Task.FromResult(new Dictionary<WindowKey, RectD>(Frames));

        public Task RestoreAsync(IReadOnlyDictionary<WindowKey, RectD> snapshot, WindowKey? except)
        {
            foreach (var kv in snapshot)
            {
                if (except != kv.Key)
                    Frames[kv.Key] = kv.Value;
            }
            return Task.CompletedTask;
        }
    }

    private sealed class FakeApps : IAppControl
    {
        public Task<int?> PidOfAsync(AppInfo app) => Task.FromResult<int?>(null);
        public Task<IReadOnlyList<int>> PidsAsync(string appId) => Task.FromResult<IReadOnlyList<int>>(Array.Empty<int>());
        public Task<int?> OpenAsync(AppInfo app) => Task.FromResult<int?>(null);
        public Task<bool> NewWindowAsync(int pid) => Task.FromResult(false);
        public Task<bool> QuitAsync(int pid) => Task.FromResult(true);
        public Task<bool> ForceQuitAsync(int pid) => Task.FromResult(true);
        public Task<bool> IsRunningAsync(int pid) => Task.FromResult(true);
    }
}
