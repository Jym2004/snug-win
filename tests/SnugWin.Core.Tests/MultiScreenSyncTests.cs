using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SnugWin.Core.Config;
using SnugWin.Core.Persistence;
using SnugWin.Core.PhoneScreen;
using Xunit;

namespace SnugWin.Core.Tests;

public sealed class MultiScreenSyncTests : IDisposable
{
    private readonly string _tempDir;
    private readonly SnugPaths _paths;

    public MultiScreenSyncTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "snugwin-multiscreen-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _paths = new SnugPaths(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void ForController_KeepsOnlyMatchingScreensWhileExemptingPersistentMain()
    {
        var touch = new ScreenWish("t", "Phone", PhoneChoice.Standard, ScreenOrientation.Portrait,
            ScreenFit.Standard, ScreenBehavior.TouchScreen);
        var computer = new ScreenWish("c", "Laptop", PhoneChoice.Standard, ScreenOrientation.Landscape,
            ScreenFit.Standard, ScreenBehavior.Computer);
        var mainTouch = touch with { Id = "m", IsMain = true };

        var forPhone = ScreenPlan.ForController([touch, computer], wantsTouch: true);
        Assert.Equal(["t"], forPhone.Select(w => w.Id));

        var forDesktop = ScreenPlan.ForController([touch, computer], wantsTouch: false);
        Assert.Equal(["c"], forDesktop.Select(w => w.Id));

        // Do not leak a phone screen to a desktop controller when only phone screens are configured.
        Assert.Empty(ScreenPlan.ForController([touch], wantsTouch: false));
        Assert.Single(ScreenPlan.ForController([touch], wantsTouch: true));
        Assert.Single(ScreenPlan.ForController([computer], wantsTouch: false));
        Assert.Empty(ScreenPlan.ForController([computer], wantsTouch: true));

        // Persistent main screen and non-remote standby must never be dropped.
        Assert.Equal(["m", "c"], ScreenPlan.ForController([mainTouch, touch, computer], wantsTouch: false).Select(w => w.Id));
        Assert.Equal(["m"], ScreenPlan.ForController([mainTouch], wantsTouch: false, remote: false).Select(w => w.Id));

        Assert.Equal(["t"], ScreenPlan.ForManualMode([touch, computer], ManualRemoteScreenMode.TouchScreen).Select(w => w.Id));
        Assert.Equal(["c"], ScreenPlan.ForManualMode([touch, computer], ManualRemoteScreenMode.Computer).Select(w => w.Id));
        Assert.Empty(ScreenPlan.ForManualMode([touch, computer], ManualRemoteScreenMode.Off));
        Assert.Empty(ScreenPlan.ForManualMode([touch], ManualRemoteScreenMode.Computer));
    }

    [Fact]
    public void DesiredWishes_MergesUuAndManualRequestsAndKeepsMainOnShutdown()
    {
        var phone = new PhoneChoice("Generic Phone", new PhoneScreenSpecData(1080, 2400, 360), DeviceKind.Phone);
        var computer = new PhoneChoice("Generic Desktop", new PhoneScreenSpecData(1920, 1080), DeviceKind.Computer);
        var config = new AppConfig()
            .WithMainScreen(ScreenConfig.LegacyPhoneId)
            .WithScreenAdded(ScreenOrientation.Portrait, phone, "Phone Screen")
            .WithScreenAdded(ScreenOrientation.Landscape, computer, "Computer Screen");

        var standby = ScreenPlan.DesiredWishes(config, uuRemoteActive: false, uuWantsTouch: false,
            ManualRemoteScreenMode.Off);
        Assert.Equal([ScreenConfig.LegacyPhoneId], standby.Select(wish => wish.Id));
        Assert.False(ScreenPlan.IsRemoteActive(false, ManualRemoteScreenMode.Off));

        var combined = ScreenPlan.DesiredWishes(config, uuRemoteActive: true, uuWantsTouch: false,
            ManualRemoteScreenMode.TouchScreen);
        Assert.Equal(3, combined.Count);
        Assert.Equal(3, combined.Select(wish => wish.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Contains(combined, wish => wish.IsMain);
        Assert.Contains(combined, wish => wish.IsTouch && !wish.IsMain);
        Assert.Contains(combined, wish => !wish.IsTouch);
        Assert.True(ScreenPlan.IsRemoteActive(true, ManualRemoteScreenMode.Off));

        var afterManualOff = ScreenPlan.DesiredWishes(config, uuRemoteActive: true, uuWantsTouch: false,
            ManualRemoteScreenMode.Off);
        Assert.Equal(2, afterManualOff.Count);
        Assert.Contains(afterManualOff, wish => wish.IsMain);
        Assert.Contains(afterManualOff, wish => !wish.IsTouch);

        Assert.True(ScreenPlan.HasManualModeTarget(ScreenPlan.Wishes(config, remote: true),
            ManualRemoteScreenMode.TouchScreen));
        Assert.True(ScreenPlan.HasManualModeTarget(ScreenPlan.Wishes(config, remote: true),
            ManualRemoteScreenMode.Computer));
        Assert.False(ScreenPlan.HasManualModeTarget(ScreenPlan.Wishes(config, remote: true),
            ManualRemoteScreenMode.Off));
        Assert.True(ScreenPlan.CanReenterPhoneScreen(false, false, ManualRemoteScreenMode.TouchScreen, true));
        Assert.False(ScreenPlan.CanReenterPhoneScreen(false, false, ManualRemoteScreenMode.Computer, true));
    }

    [Fact]
    public void PhoneCatalog_LoadsUpstreamDeviceModelsJson_HasPhoneTabletComputer()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Assets", "device-models.json");
        Assert.True(File.Exists(path), $"Expected device-models.json at {path}");

        string json = File.ReadAllText(path);
        var catalog = PhoneCatalog.FromJson(json);

        var phones = catalog.BrandsIn(DeviceKind.Phone).SelectMany(b => b.Models).ToList();
        var tablets = catalog.BrandsIn(DeviceKind.Tablet).SelectMany(b => b.Models).ToList();
        var computers = catalog.BrandsIn(DeviceKind.Computer).SelectMany(b => b.Models).ToList();

        Assert.True(phones.Count > 500, $"Expected >500 phones, got {phones.Count}");
        Assert.True(tablets.Count > 20, $"Expected >20 tablets, got {tablets.Count}");
        Assert.True(computers.Count > 5, $"Expected >5 computers, got {computers.Count}");

        Assert.All(phones, m => Assert.Equal(DeviceKind.Phone, m.Kind));
        Assert.All(tablets, m => Assert.Equal(DeviceKind.Tablet, m.Kind));
        Assert.All(computers, m => Assert.Equal(DeviceKind.Computer, m.Kind));

        var tabletHits = catalog.Search("iPad", new HashSet<DeviceKind> { DeviceKind.Tablet });
        Assert.NotEmpty(tabletHits);
        Assert.All(tabletHits.SelectMany(b => b.Models), m => Assert.Equal(DeviceKind.Tablet, m.Kind));

        var computerHits = catalog.Search("1080", new HashSet<DeviceKind> { DeviceKind.Computer });
        Assert.NotEmpty(computerHits);
    }

    [Fact]
    public void ScreenFit_FromDevice_ComputesLandscapePhoneTabletAndComputerModes()
    {
        var phoneFitPortrait = ScreenFit.FromDevice(
            new PhoneScreenSpecData(1206, 2622, 402),
            DeviceKind.Phone,
            ScreenOrientation.Portrait);
        var phoneFitLandscape = ScreenFit.FromDevice(
            new PhoneScreenSpecData(1206, 2622, 402),
            DeviceKind.Phone,
            ScreenOrientation.Landscape);

        Assert.Equal(ScreenOrientation.Portrait, phoneFitPortrait.Orientation);
        Assert.Equal(ScreenOrientation.Landscape, phoneFitLandscape.Orientation);
        Assert.True(phoneFitLandscape.Ratio < 1.0);
        Assert.Equal(phoneFitLandscape.Widths.Reverse().ToList(), phoneFitLandscape.RegistrationOrder);
        Assert.True(phoneFitLandscape.RegistrationOrder[0] >= phoneFitLandscape.RegistrationOrder[^1]);

        var tabletFit = ScreenFit.FromDevice(
            new PhoneScreenSpecData(1668, 2388, 834),
            DeviceKind.Tablet,
            ScreenOrientation.Landscape);
        Assert.Equal(DeviceKind.Tablet, tabletFit.Kind);
        Assert.True(tabletFit.BaseWidth >= ScreenFit.MinLongSide);
        Assert.Equal(200, tabletFit.TargetDpiPercent(tabletFit.BaseWidth));

        var comp1080 = ScreenFit.FromDevice(
            new PhoneScreenSpecData(1920, 1080),
            DeviceKind.Computer,
            ScreenOrientation.Landscape);
        Assert.Single(comp1080.Widths);
        Assert.Equal(200, comp1080.TargetDpiPercent(comp1080.BaseWidth));

        var comp4k = ScreenFit.FromDevice(
            new PhoneScreenSpecData(3840, 2160),
            DeviceKind.Computer,
            ScreenOrientation.Landscape);
        Assert.Single(comp4k.Widths);
        Assert.Equal(100, comp4k.TargetDpiPercent(comp4k.BaseWidth));
    }

    [Fact]
    public void AppConfig_MigratesLegacyAndManagesUpToFourScreens()
    {
        var legacyJson = """
        {
          "phoneScreen": false,
          "phone": { "model": "Apple iPhone 16", "screen": { "width": 1179, "height": 2556, "points": 393 } }
        }
        """;
        var migrated = AppConfig.FromJsonOrDefault(legacyJson);
        var screens = migrated.EffectiveScreens();
        Assert.Single(screens);
        Assert.Equal(ScreenConfig.LegacyPhoneId, screens[0].Id);
        Assert.False(screens[0].Enabled);
        Assert.Equal("Apple iPhone 16", screens[0].Device.Model);

        var tablet = new PhoneChoice("iPad Pro 11\"", new PhoneScreenSpecData(1668, 2388, 834), DeviceKind.Tablet);
        var desktop = new PhoneChoice("FHD 1080p", new PhoneScreenSpecData(1920, 1080), DeviceKind.Computer);
        var phone2 = new PhoneChoice("Phone 2", new PhoneScreenSpecData(1080, 2400, 360), DeviceKind.Phone);
        var extra = new PhoneChoice("Phone 5", new PhoneScreenSpecData(1080, 2400, 360), DeviceKind.Phone);

        var cur = migrated
            .WithScreenAdded(ScreenOrientation.Landscape, tablet, "我的平板")
            .WithScreenAdded(ScreenOrientation.Portrait, desktop, "电脑副屏") // computer forces landscape
            .WithScreenAdded(ScreenOrientation.Landscape, phone2);

        Assert.Equal(4, cur.EffectiveScreens().Count);
        Assert.Equal(ScreenOrientation.Landscape, cur.EffectiveScreens()[2].Orientation);

        // 5th screen exceeds ScreenConfig.Limit (4) and is rejected
        var rejected = cur.WithScreenAdded(ScreenOrientation.Portrait, extra);
        Assert.Equal(4, rejected.EffectiveScreens().Count);

        string removedId = cur.EffectiveScreens()[1].Id;
        var afterRemove = cur.WithScreenRemoved(removedId);
        Assert.Equal(3, afterRemove.EffectiveScreens().Count);
    }

    [Fact]
    public void ScreenPlan_Wishes_HonorsEnabledLimitAndBehavior()
    {
        var tablet = new PhoneChoice("iPad Air", new PhoneScreenSpecData(1640, 2360, 820), DeviceKind.Tablet);
        var computer = new PhoneChoice("1080P", new PhoneScreenSpecData(1920, 1080), DeviceKind.Computer);

        var cfg = new AppConfig()
            .WithScreenAdded(ScreenOrientation.Landscape, tablet, "平板")
            .WithScreenAdded(ScreenOrientation.Landscape, computer, "桌面副屏");

        // Disable the first legacy screen
        cfg = cfg.WithScreenUpdated(ScreenConfig.LegacyPhoneId, s => s with { Enabled = false });

        Assert.Empty(ScreenPlan.Wishes(cfg, remote: false, preview: false));

        var wishes = ScreenPlan.Wishes(cfg, remote: true);
        Assert.Equal(2, wishes.Count);
        Assert.Equal(ScreenBehavior.TouchScreen, wishes[0].Behavior);
        Assert.Equal(ScreenBehavior.Computer, wishes[1].Behavior);
        Assert.True(wishes[0].IsTouch);
        Assert.False(wishes[1].IsTouch);
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

    private sealed class FakeMultiDisplays : IPhoneDisplays
    {
        private uint _nextId = 2000;
        public Dictionary<uint, ScreenFit> ActiveFits { get; } = new();
        public Dictionary<uint, int> Widths { get; } = new();
        public Dictionary<uint, string?> Names { get; } = new();

        public Task<uint> CreateAsync(ScreenFit fit, CancellationToken ct = default) =>
            CreateAsync(fit, null, ct);

        public Task<uint> CreateAsync(ScreenFit fit, string? displayName, CancellationToken ct = default)
        {
            uint id = _nextId++;
            ActiveFits[id] = fit;
            Widths[id] = fit.BaseWidth;
            Names[id] = displayName;
            return Task.FromResult(id);
        }

        public Task<bool> ReshapeAsync(uint displayId, ScreenFit fit, CancellationToken ct = default)
        {
            if (!ActiveFits.ContainsKey(displayId)) return Task.FromResult(false);
            ActiveFits[displayId] = fit;
            Widths[displayId] = fit.BaseWidth;
            return Task.FromResult(true);
        }

        public ScreenFit? FitOf(uint displayId) =>
            ActiveFits.TryGetValue(displayId, out var fit) ? fit : null;

        public Task<bool> RetireAsync(uint displayId, CancellationToken ct = default)
        {
            ActiveFits.Remove(displayId);
            Widths.Remove(displayId);
            return Task.FromResult(true);
        }

        public bool IsOnline(uint displayId) => ActiveFits.ContainsKey(displayId);
        public int? WidthOf(uint displayId) => Widths.TryGetValue(displayId, out int w) ? w : null;

        public Task SetWidthAsync(uint displayId, int width, CancellationToken ct = default)
        {
            if (ActiveFits.ContainsKey(displayId)) Widths[displayId] = width;
            return Task.CompletedTask;
        }

        public RectD? BoundsOf(uint displayId)
        {
            if (!ActiveFits.TryGetValue(displayId, out var fit)) return null;
            int w = Widths.TryGetValue(displayId, out int cur) ? cur : fit.BaseWidth;
            var sz = fit.Size(w);
            double xOffset = 3000 + (displayId - 2000) * 1500;
            return new RectD(xOffset, 0, sz.Width * 2, sz.Height * 2);
        }

        public RectD? WindowAreaOf(uint displayId)
        {
            if (BoundsOf(displayId) is not { } b || !ActiveFits.TryGetValue(displayId, out var fit))
                return null;
            if (fit.Kind == DeviceKind.Computer) return b;
            int w = Widths[displayId];
            double bar = ScreenFit.HomeBarHeight(fit.Size(w)) * 2;
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

        public Task<bool> ExistsAsync(WindowKey window) => Task.FromResult(Frames.ContainsKey(window));
        public Task<bool> IsShownAsync(WindowKey window) => Task.FromResult(Frames.ContainsKey(window));
        public Task<WindowLook?> LookAsync(WindowKey window) =>
            Task.FromResult(Frames.TryGetValue(window, out var r) ? new WindowLook(r) : null);

        public Task<RectD?> MoveAsync(WindowKey window, RectD frame)
        {
            if (!Frames.ContainsKey(window)) return Task.FromResult<RectD?>(null);
            Frames[window] = frame;
            return Task.FromResult<RectD?>(frame);
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
    public async Task DeskSet_MultiScreen_CreateTransferAndRetireWorkCleanly()
    {
        var displays = new FakeMultiDisplays();
        var windows = new FakeWindows();
        var timeline = new FakeTimeline();
        var desks = new DeskSet(displays, windows, new FakeApps(), _paths, timeline, defaultZoom: 2);

        var tabletChoice = new PhoneChoice("iPad Pro 11\"", new PhoneScreenSpecData(1668, 2388, 834), DeviceKind.Tablet);
        var cfg = new AppConfig()
            .WithScreenAdded(ScreenOrientation.Landscape, tabletChoice, "平板屏");

        var wishes = ScreenPlan.Wishes(cfg, remote: true);
        Assert.Equal(2, wishes.Count);

        desks.SetRemote(true);
        desks.SetWishes(wishes);
        await Task.Delay(120);

        string idA = wishes[0].Id;
        string idB = wishes[1].Id;
        Assert.Equal(PhoneScreenPhase.Ready, desks.StateOf(idA)?.ScreenPhase);
        Assert.Equal(PhoneScreenPhase.Ready, desks.StateOf(idB)?.ScreenPhase);
        Assert.Equal(2, displays.ActiveFits.Count);

        var key = new WindowKey(101, 8888);
        windows.Frames[key] = new RectD(40, 40, 600, 500);
        var winInfo = new WindowInfo(key, "Windows Terminal", "WindowsTerminal.exe", "snug-win", false);

        desks.Pull(idA, winInfo);
        await Task.Delay(120);
        Assert.Equal(key, desks.StateOf(idA)?.Placed?.Window.Key);
        Assert.Null(desks.StateOf(idB)?.Placed);
        Assert.Equal(2, windows.ZoomDeltaTotal);

        // Transfer exact same window from screen A to screen B without double-zooming
        desks.Pull(idB, winInfo);
        await Task.Delay(120);
        Assert.Null(desks.StateOf(idA)?.Placed);
        Assert.Equal(key, desks.StateOf(idB)?.Placed?.Window.Key);
        Assert.Equal(2, windows.ZoomDeltaTotal);

        await desks.ShutdownAsync();
        Assert.Empty(displays.ActiveFits);
    }
}
