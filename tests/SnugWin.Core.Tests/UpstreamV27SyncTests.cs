using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SnugWin.Core.Config;
using SnugWin.Core.Persistence;
using SnugWin.Core.PhoneScreen;
using SnugWin.Core.Session;
using SnugWin.Core.Support;
using SnugWin.Core.Touch;
using Xunit;

namespace SnugWin.Core.Tests;

public sealed class UpstreamV27SyncTests : IDisposable
{
    private readonly string _tempDir;
    private readonly SnugPaths _paths;

    public UpstreamV27SyncTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "snugwin-v27-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _paths = new SnugPaths(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void SessionEpisode_EndNow_EndsImmediatelyOnlyWhenInterrupted()
    {
        var ep = new SessionEpisode();
        var t0 = new DateTimeOffset(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);

        Assert.Null(ep.EndNow());

        Assert.Equal(EpisodeChange.Began, ep.Connected(t0));
        Assert.False(ep.IsInterrupted);
        Assert.Null(ep.EndNow());

        Assert.Equal(EpisodeChange.Interrupted, ep.Disconnected(t0.AddSeconds(5)));
        Assert.True(ep.IsInterrupted);
        Assert.True(ep.IsActive);

        Assert.Equal(EpisodeChange.Ended, ep.EndNow());
        Assert.False(ep.IsInterrupted);
        Assert.False(ep.IsActive);
        Assert.Null(ep.EndNow());
    }

    [Fact]
    public void RemoteSession_EndNow_ClearsStateAndReturnsEnded()
    {
        var session = new RemoteSession();
        var t0 = new DateTimeOffset(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);

        var connectedSig = new UUSignals(true, ControllerTable.FromNames("iPhone"), true);
        var disconnectedSig = new UUSignals(false, ControllerTable.FromNames(), false);

        session.Update(connectedSig, t0);
        Assert.True(session.InEpisode);
        Assert.False(session.IsInReconnectGrace);

        // Trigger disconnect past debounce window
        session.Update(disconnectedSig, t0.AddSeconds(1));
        var trans = session.Update(disconnectedSig, t0.AddSeconds(5));
        Assert.Contains(trans, x => x is SessionTransition.Interrupted);
        Assert.True(session.IsInReconnectGrace);

        var ended = session.EndNow();
        Assert.IsType<SessionTransition.Ended>(ended);
        Assert.False(session.InEpisode);
        Assert.False(session.IsInReconnectGrace);
        Assert.Null(session.EpisodeBeganAt);
    }

    [Fact]
    public void CursorHome_Landing_ReturnsNullWhenInsideKeptAndCenterWhenOutside()
    {
        var main = new RectD(0, 0, 1920, 1080);
        var secondary = new RectD(1920, 0, 1440, 900);
        var kept = new List<RectD> { main, secondary };

        // Already inside kept monitor -> null
        Assert.Null(CursorHome.Landing(new PointD(500, 500), kept, main));
        Assert.Null(CursorHome.Landing(new PointD(2100, 200), kept, main));

        // Stranded on retired virtual monitor -> center of main
        var landing = CursorHome.Landing(new PointD(4000, 300), kept, main);
        Assert.NotNull(landing);
        Assert.Equal(960, landing.Value.X);
        Assert.Equal(540, landing.Value.Y);

        // Main itself retired (empty) -> center of first kept
        var fallback = CursorHome.Landing(new PointD(4000, 300), new List<RectD> { secondary }, default);
        Assert.NotNull(fallback);
        Assert.Equal(2640, fallback.Value.X);
        Assert.Equal(450, fallback.Value.Y);

        // Nothing kept -> null
        Assert.Null(CursorHome.Landing(new PointD(4000, 300), Array.Empty<RectD>(), default));
    }

    [Fact]
    public void UUProcessWatch_CachesAlivePidAndRespectsCooldownWhenAbsent()
    {
        int scans = 0;
        int? fakePid = null;
        bool alive = false;

        var watch = new UUProcessWatch(
            scan: () => { scans++; return fakePid; },
            isAlive: pid => alive && pid == fakePid,
            rescanCooldown: TimeSpan.FromSeconds(3));

        var t0 = new DateTimeOffset(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);

        // First call scans, finds nothing
        Assert.Null(watch.Resolve(t0));
        Assert.Equal(1, scans);

        // Within cooldown -> no rescan
        Assert.Null(watch.Resolve(t0.AddSeconds(1)));
        Assert.Null(watch.Resolve(t0.AddSeconds(2.5)));
        Assert.Equal(1, scans);

        // After cooldown -> rescans and finds PID 4321
        fakePid = 4321;
        alive = true;
        Assert.Equal(4321, watch.Resolve(t0.AddSeconds(3.1)));
        Assert.Equal(2, scans);

        // While alive -> returns cached PID without rescanning
        Assert.Equal(4321, watch.Resolve(t0.AddSeconds(10)));
        Assert.Equal(4321, watch.Resolve(t0.AddSeconds(20)));
        Assert.Equal(2, scans);

        // Process exits -> clears cache and rescans immediately (cooldown elapsed)
        alive = false;
        fakePid = null;
        Assert.Null(watch.Resolve(t0.AddSeconds(25)));
        Assert.Equal(3, scans);
    }

    [Fact]
    public void ScreenPlan_Wishes_KeepsPersistentMainScreenWhenNotRemote()
    {
        var tablet = new PhoneChoice("iPad Pro", new PhoneScreenSpecData(1668, 2388, 834), DeviceKind.Tablet);
        var cfg = new AppConfig()
            .WithScreenAdded(ScreenOrientation.Landscape, tablet, "副屏")
            .WithMainScreen(ScreenConfig.LegacyPhoneId);

        // Not remote: only persistent MainScreen is wished
        var idleWishes = ScreenPlan.Wishes(cfg, remote: false);
        Assert.Single(idleWishes);
        Assert.Equal(ScreenConfig.LegacyPhoneId, idleWishes[0].Id);
        Assert.True(idleWishes[0].IsMain);

        // Remote: both enabled screens are wished
        var remoteWishes = ScreenPlan.Wishes(cfg, remote: true);
        Assert.Equal(2, remoteWishes.Count);
        Assert.True(remoteWishes[0].IsMain);
        Assert.False(remoteWishes[1].IsMain);

        // Removing or disabling MainScreen clears it cleanly
        var disabled = cfg.WithScreenUpdated(ScreenConfig.LegacyPhoneId, s => s with { Enabled = false });
        Assert.Null(disabled.MainScreen);
        Assert.Empty(ScreenPlan.Wishes(disabled, remote: false));
    }

    private sealed class FakeTimeline : ITimeline
    {
        public double Now { get; set; }
        public Task SleepAsync(double seconds, CancellationToken ct = default)
        {
            Now += Math.Max(0, seconds);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeRelayDisplays : IPhoneDisplays
    {
        public uint? ActiveId { get; private set; }
        public int CurrentWidth { get; private set; } = 540;
        public ScreenFit? CurrentFit { get; private set; }
        private RectD PhoneBounds => new(3000, 0, CurrentWidth * 2, (CurrentFit ?? ScreenFit.Standard).Size(CurrentWidth).Height * 2);

        public Task<uint> CreateAsync(ScreenFit fit, CancellationToken ct = default)
        {
            ActiveId = 5001;
            CurrentFit = fit;
            CurrentWidth = fit.BaseWidth;
            return Task.FromResult(5001u);
        }

        public Task<bool> ReshapeAsync(uint displayId, ScreenFit fit, CancellationToken ct = default) => Task.FromResult(true);
        public ScreenFit? FitOf(uint displayId) => ActiveId == displayId ? CurrentFit : null;
        public Task<bool> RetireAsync(uint displayId, CancellationToken ct = default) { ActiveId = null; return Task.FromResult(true); }
        public bool IsOnline(uint displayId) => ActiveId == displayId;
        public int? WidthOf(uint displayId) => ActiveId == displayId ? CurrentWidth : null;
        public Task SetWidthAsync(uint displayId, int width, CancellationToken ct = default) { CurrentWidth = width; return Task.CompletedTask; }
        public RectD? BoundsOf(uint displayId) => ActiveId == displayId ? PhoneBounds : null;
        public RectD? WindowAreaOf(uint displayId) => ActiveId == displayId ? new RectD(PhoneBounds.X, PhoneBounds.Y, PhoneBounds.Width, PhoneBounds.Height - 56) : null;
        public RectD? MainArea() => new(0, 0, 1920, 1040);
        public IReadOnlyList<OtherDisplay> OtherDisplays() => Array.Empty<OtherDisplay>();
        public Task SetModeAsync(uint displayId, DisplayModeInfo mode, bool permanently, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FakeRelayWindows : IWindowControl
    {
        public Dictionary<WindowKey, RectD> Frames { get; } = new();
        public List<RectD> MoveHistory { get; } = new();
        private bool _firstJumpFailed;

        public Task<bool> ExistsAsync(WindowKey window) => Task.FromResult(Frames.ContainsKey(window));
        public Task<bool> IsShownAsync(WindowKey window) => Task.FromResult(Frames.ContainsKey(window));
        public Task<WindowLook?> LookAsync(WindowKey window) =>
            Task.FromResult(Frames.TryGetValue(window, out var r) ? new WindowLook(r) : null);

        public Task<RectD?> MoveAsync(WindowKey window, RectD frame)
        {
            if (!Frames.ContainsKey(window)) return Task.FromResult<RectD?>(null);
            MoveHistory.Add(frame);
            if (!_firstJumpFailed && frame.X >= 3000)
            {
                // Simulate OS clamping first direct jump from far monitor back to far monitor
                _firstJumpFailed = true;
                var stuck = new RectD(-1920, 100, frame.Width, frame.Height);
                Frames[window] = stuck;
                return Task.FromResult<RectD?>(stuck);
            }
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
        public Task<Dictionary<WindowKey, RectD>> FramesAsync(int pid) => Task.FromResult(new Dictionary<WindowKey, RectD>(Frames));
        public Task<Dictionary<WindowKey, RectD>> SnapshotAsync() => Task.FromResult(new Dictionary<WindowKey, RectD>(Frames));
        public Task RestoreAsync(IReadOnlyDictionary<WindowKey, RectD> snapshot, WindowKey? except) => Task.CompletedTask;
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

    [Fact]
    public async Task PhoneDesk_FillAsync_RelaysThroughMainWhenFarDisplayJumpMissesTarget()
    {
        var displays = new FakeRelayDisplays();
        var windows = new FakeRelayWindows();
        var desk = new PhoneDesk(displays, windows, new FakeApps(), _paths, new FakeTimeline(), defaultZoom: 0);

        desk.SetRemote(true);
        desk.SetWanted(true);
        await Task.Delay(120);
        Assert.Equal(PhoneScreenPhase.Ready, desk.State.ScreenPhase);

        var key = new WindowKey(202, 7777);
        windows.Frames[key] = new RectD(-1920, 100, 800, 600);
        var info = new WindowInfo(key, "Notepad", "notepad.exe", "notes.txt", false);

        desk.Pull(info);
        await Task.Delay(120);

        Assert.Equal(key, desk.State.Placed?.Window.Key);
        // Should have: 1) initial jump to 3000 (failed), 2) relay onto MainArea (0..1920), 3) retry to 3000 (succeeded)
        Assert.True(windows.MoveHistory.Count >= 3);
        Assert.Contains(windows.MoveHistory, r => r.X >= 0 && r.X < 1920);
        Assert.True(windows.Frames[key].X >= 3000);

        await desk.ShutdownAsync();
    }
}
