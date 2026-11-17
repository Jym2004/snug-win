using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SnugWin.Core.Persistence;
using SnugWin.Core.PhoneScreen;
using Xunit;

namespace SnugWin.Core.Tests;

public sealed class PhoneDeskTests : IDisposable
{
    private readonly string _tempDir;
    private readonly SnugPaths _paths;

    public PhoneDeskTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "snugwin-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _paths = new SnugPaths(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
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

    private sealed class FakeDisplays : IPhoneDisplays
    {
        public uint? ActiveId { get; private set; }
        public int CurrentWidth { get; private set; } = 540;
        public ScreenFit? CurrentFit { get; private set; }
        public RectD PhoneBounds => new(2560, 0, CurrentWidth * 2, (CurrentFit ?? ScreenFit.Standard).Size(CurrentWidth).Height * 2);

        public Task<uint> CreateAsync(ScreenFit fit, CancellationToken ct = default)
        {
            ActiveId = 1001;
            CurrentFit = fit;
            CurrentWidth = fit.BaseWidth;
            return Task.FromResult(1001u);
        }

        public Task<bool> ReshapeAsync(uint displayId, ScreenFit fit, CancellationToken ct = default)
        {
            if (ActiveId != displayId) return Task.FromResult(false);
            CurrentFit = fit;
            CurrentWidth = fit.BaseWidth;
            return Task.FromResult(true);
        }

        public ScreenFit? FitOf(uint displayId) => ActiveId == displayId ? CurrentFit : null;

        public Task<bool> RetireAsync(uint displayId, CancellationToken ct = default)
        {
            if (ActiveId == displayId) ActiveId = null;
            return Task.FromResult(true);
        }

        public bool IsOnline(uint displayId) => ActiveId == displayId;
        public int? WidthOf(uint displayId) => ActiveId == displayId ? CurrentWidth : null;

        public Task SetWidthAsync(uint displayId, int width, CancellationToken ct = default)
        {
            if (ActiveId == displayId) CurrentWidth = width;
            return Task.CompletedTask;
        }

        public RectD? BoundsOf(uint displayId) => ActiveId == displayId ? PhoneBounds : null;

        public RectD? WindowAreaOf(uint displayId)
        {
            if (ActiveId != displayId) return null;
            var b = PhoneBounds;
            double bar = ScreenFit.HomeBarHeight(CurrentWidth) * 2;
            return new RectD(b.X, b.Y, b.Width, b.Height - bar);
        }

        public RectD? MainArea() => new(0, 0, 1920, 1040);
        public IReadOnlyList<OtherDisplay> OtherDisplays() => Array.Empty<OtherDisplay>();
        public Task SetModeAsync(uint displayId, DisplayModeInfo mode, bool permanently, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FakeWindows : IWindowControl
    {
        public Dictionary<WindowKey, RectD> Frames { get; } = new();
        public int ZoomDeltaTotal { get; private set; }
        public double MinLogicalWidth { get; set; }

        public Task<bool> ExistsAsync(WindowKey window) => Task.FromResult(Frames.ContainsKey(window));
        public Task<bool> IsShownAsync(WindowKey window) => Task.FromResult(Frames.ContainsKey(window));

        public Task<WindowLook?> LookAsync(WindowKey window) =>
            Task.FromResult(Frames.TryGetValue(window, out var r) ? new WindowLook(r) : null);

        public Task<RectD?> MoveAsync(WindowKey window, RectD frame)
        {
            if (!Frames.ContainsKey(window)) return Task.FromResult<RectD?>(null);
            double clampedW = Math.Max(frame.Width, MinLogicalWidth * 2);
            var got = new RectD(frame.X, frame.Y, clampedW, frame.Height);
            Frames[window] = got;
            return Task.FromResult<RectD?>(got);
        }

        public Task SetFullScreenAsync(WindowKey window, bool on) => Task.CompletedTask;
        public Task SetMinimizedAsync(WindowKey window, bool on) => Task.CompletedTask;
        public Task FocusAsync(WindowKey window) => Task.CompletedTask;

        public Task<bool> ZoomAsync(WindowKey window, int steps)
        {
            ZoomDeltaTotal += steps;
            return Task.FromResult(true);
        }

        public Task<IReadOnlyList<WindowInfo>> ShownWindowsAsync(int pid) =>
            Task.FromResult<IReadOnlyList<WindowInfo>>(Array.Empty<WindowInfo>());

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
    public async Task Pull_StepsUpWidthWhenClamped_ZoomsAndRestoresOnPutBack()
    {
        var displays = new FakeDisplays();
        var windows = new FakeWindows();
        var timeline = new FakeTimeline();
        var desk = new PhoneDesk(displays, windows, new FakeApps(), _paths, timeline, defaultZoom: 2);

        var winKey = new WindowKey(42, 0x100);
        var original = new RectD(100, 100, 1500, 700);
        windows.Frames[winKey] = original;
        windows.MinLogicalWidth = 700; // clamps 540 -> triggers step-up to 700

        desk.SetWanted(true);
        await desk.IdleAsync();
        Assert.Equal(PhoneScreenPhase.Ready, desk.State.ScreenPhase);

        var info = new WindowInfo(winKey, "VS Code", "Code.exe", "README.md");
        desk.Pull(info);
        await desk.IdleAsync();

        Assert.Equal(700, displays.CurrentWidth);
        Assert.Equal(2, windows.ZoomDeltaTotal);
        Assert.False(desk.State.ListVisible);
        Assert.NotNull(desk.State.Placed);

        desk.PutBack();
        await desk.IdleAsync();

        Assert.Null(desk.State.Placed);
        Assert.True(desk.State.ListVisible);
        Assert.Equal(540, displays.CurrentWidth);
        Assert.Equal(0, windows.ZoomDeltaTotal);
        Assert.Equal(original, windows.Frames[winKey]);
        Assert.False(File.Exists(_paths.PhoneRecordFile));
    }

    [Fact]
    public async Task Recover_RestoresLeftoverRecordAfterSimulatedCrash()
    {
        var displays = new FakeDisplays();
        var windows = new FakeWindows();
        var timeline = new FakeTimeline();

        var winKey = new WindowKey(88, 0x200);
        var original = new RectD(50, 60, 800, 600);
        windows.Frames[winKey] = new RectD(2560, 0, 1080, 1800);

        var ledger = new RestoreRecordStore<PhoneRecord>(_paths.PhoneRecordFile);
        ledger.Record(new PhoneRecord
        {
            Window = winKey,
            App = "Windows Terminal",
            Label = "Terminal",
            Original = original,
            Zoom = 2,
            AppId = "WindowsTerminal.exe"
        });

        var desk = new PhoneDesk(displays, windows, new FakeApps(), _paths, timeline);
        desk.Recover();
        await desk.IdleAsync();

        Assert.Equal(original, windows.Frames[winKey]);
        Assert.Equal(-2, windows.ZoomDeltaTotal);
        Assert.Null(ledger.Pending());
    }
}
