using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Threading.Tasks;
using System.Windows.Threading;
using Microsoft.Win32;
using SnugWin.App.Views;
using SnugWin.Core.Config;
using SnugWin.Core.Persistence;
using SnugWin.Core.PhoneScreen;
using SnugWin.Core.Session;
using SnugWin.Core.Support;
using SnugWin.Core.Touch;
using SnugWin.Windows.Display;
using SnugWin.Windows.Input;
using SnugWin.Windows.Sensing;
using SnugWin.Windows.Windows;

namespace SnugWin.App.Controllers;

public sealed class AppController : ILogSink
{
    private readonly Dispatcher _dispatcher;
    private readonly SnugPaths _paths;
    private readonly JsonFileStore<AppConfig> _configStore;
    private readonly PhoneDisplayService _displays;
    private readonly WindowService _windows;
    private readonly AppService _apps;
    private readonly TouchHookService _touchHook;
    private readonly UUSensor _sensor;
    private readonly RemoteSession _session = new();
    private readonly PhoneDesk _desk;
    private readonly Screenshots _screenshots;
    private readonly DispatcherTimer _tickTimer;
    private readonly List<string> _recentLogs = new();
    private readonly object _logGate = new();

    private AppConfig _config;
    private DateTimeOffset _lastVerifyAt = DateTimeOffset.MinValue;
    private DateTimeOffset _lastCatalogRefresh = DateTimeOffset.MinValue;
    private IReadOnlyList<AppInfo> _installedApps = Array.Empty<AppInfo>();
    private IReadOnlyList<RunningApp> _runningApps = Array.Empty<RunningApp>();
    private bool _catalogLoaded;
    private bool _reorderRunningApps = true;
    private bool _wasListShowing;
    private PhoneHomeWindow? _phoneHomeWindow;
    private HomeBarWindow? _homeBarWindow;
    private ScreenshotOverlayWindow? _screenshotOverlayWindow;
    private CropWindow? _cropWindow;
    private CopySelectionBubbleWindow? _copyBubbleWindow;
    private CapturedImage? _cropWindowImage;
    private bool _shuttingDown;
    private int _sampling;
    private PhoneDeskState? _lastSyncedPhoneState;
    private RectD? _lastSyncedBounds;
    private ScreenshotOverlay? _lastSyncedOverlay;
    private bool _lastSyncedScreenshotSetting;

    public PhoneCatalog Catalog { get; }
    public AppIconCache Icons { get; } = new();
    public AppConfig Config => _config;
    public PhoneDeskState DeskState => _desk.State;
    public bool ManualOverride => _sensor.ManualOverride;
    public bool CatalogLoaded => _catalogLoaded;
    public bool RequireInjectedInput
    {
        get => _touchHook.RequireInjectedFlag;
        set => _touchHook.RequireInjectedFlag = value;
    }

    public bool IsDriverPipeAvailable => VirtualDisplayIpcClient.IsDriverPipeAvailable();
    public bool IsElevated => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
    public string LogDirectory => _paths.LogDirectory;
    public IReadOnlyList<RunningApp> RunningApps => _runningApps;
    public IReadOnlyList<AppInfo> InstalledApps => _installedApps;

    public event Action<string>? StateSummaryChanged;
    public event Action? UiStateChanged;
    public event Action<string>? LogAppended;

    public AppController(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _paths = SnugPaths.Default();
        Directory.CreateDirectory(_paths.DataDirectory);
        Directory.CreateDirectory(_paths.LogDirectory);
        Log.SetSink(this);

        _configStore = new JsonFileStore<AppConfig>(_paths.ConfigFile);
        _config = (_configStore.LoadOrQuarantine() ?? new AppConfig()).Sanitize();

        Catalog = LoadPhoneCatalog();
        _config = _config with { Phone = Catalog.Resolve(_config.Phone) };
        _configStore.Save(_config);

        _displays = new PhoneDisplayService();
        _windows = new WindowService();
        _apps = new AppService(_windows);
        _touchHook = new TouchHookService();
        _sensor = new UUSensor();

        _desk = new PhoneDesk(
            _displays,
            _windows,
            _apps,
            _paths,
            defaultZoom: _config.ZoomSteps,
            fit: ScreenFit.FromPhone(_config.Phone.Screen));
        _screenshots = new Screenshots(new ScreenshotService());
        _screenshots.Changed += OnScreenshotChanged;

        _desk.StateChanged += OnDeskStateChanged;
        _desk.TopologyChanging += () => _session.NoteSelfInducedChange(DateTimeOffset.Now);
        _desk.Gone += OnDeskGone;
        _touchHook.CloseWindowRequested += handle => _dispatcher.BeginInvoke(() => _desk.ClosePlaced(handle));
        _touchHook.SelectionCompleted += pt => _dispatcher.BeginInvoke(() => ShowCopySelectionBubble(pt));
        _touchHook.SelectionDismissed += () => _dispatcher.BeginInvoke(() => _copyBubbleWindow?.HideBubble());
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

        _tickTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _tickTimer.Tick += (_, _) => OnTick();
    }

    public async void Start()
    {
        Log.Info($"SnugWin 启动（权限：{(IsElevated ? "管理员" : "普通用户")}，驱动管道：{(IsDriverPipeAvailable ? "就绪" : "未检测到")}）");
        await _displays.CleanupOrphansAsync();
        _ = Task.Run(() =>
        {
            var scanned = AppService.ScanInstalledApps();
            Icons.Preload(scanned);
            _dispatcher.BeginInvoke(() =>
            {
                _installedApps = scanned;
                UiStateChanged?.Invoke();
            });
        });

        _desk.Recover();
        _tickTimer.Start();
        NotifySummary();
    }

    private async void OnTick()
    {
        if (_shuttingDown || System.Threading.Interlocked.Exchange(ref _sampling, 1) == 1) return;
        try
        {
            var now = DateTimeOffset.Now;
            _screenshots.Expire();
            var signals = await Task.Run(() => _sensor.Sample(now));
            if (_shuttingDown) return;

            var transitions = _session.Update(signals, now);
            bool ended = transitions.Any(t => t is SessionTransition.Ended);
            if (ended) _desk.SetWanted(false);

            bool wantsPhone = _config.PhoneScreen
                              && _session.InEpisode
                              && ControllerClassifier.WantsPhoneScreen(_session.EpisodeControllers(now));
            _desk.SetWanted(wantsPhone);
            _desk.SetRemote(_session.InEpisode);

            if ((now - _lastVerifyAt).TotalSeconds >= 2.0)
            {
                _lastVerifyAt = now;
                _desk.VerifyPlacement();
                _desk.CheckReturns();
                _desk.SettleDisplays();
            }

            var st = _desk.State;
            bool showList = st.ScreenPhase == PhoneScreenPhase.Ready && (st.ListVisible || st.Placed is null);
            if (showList && st.Work is null && (now - _lastCatalogRefresh).TotalSeconds >= 1.5)
            {
                _lastCatalogRefresh = now;
                await RefreshRunningAppsAsync();
            }

            SyncTouchRegion();
            SyncPhoneWindows(forceSyncUi: false);
            NotifySummary();
        }
        finally
        {
            System.Threading.Interlocked.Exchange(ref _sampling, 0);
        }
    }

    public void RefreshRunningApps() => _ = RefreshRunningAppsAsync();

    private async Task RefreshRunningAppsAsync()
    {
        var fresh = await Task.Run(() =>
        {
            var list = _apps.GetRunningApps();
            Icons.Preload(list);
            return list;
        });
        bool reorder = _reorderRunningApps;
        _reorderRunningApps = false;
        var next = reorder ? fresh : AppList.KeepingOrder(_runningApps, fresh);
        bool changed = !RunningAppsEqual(_runningApps, next);
        _runningApps = next;
        bool loadedChanged = !_catalogLoaded;
        _catalogLoaded = true;
        if (changed || loadedChanged)
            UiStateChanged?.Invoke();
    }

    private static bool RunningAppsEqual(IReadOnlyList<RunningApp> a, IReadOnlyList<RunningApp> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
        {
            if (a[i].Pid != b[i].Pid || !string.Equals(a[i].App.AppId, b[i].App.AppId, StringComparison.OrdinalIgnoreCase))
                return false;
            if (a[i].Windows.Count != b[i].Windows.Count) return false;
            for (int j = 0; j < a[i].Windows.Count; j++)
            {
                if (a[i].Windows[j] != b[i].Windows[j]) return false;
            }
        }
        return true;
    }

    private void OnDeskStateChanged(PhoneDeskState state)
    {
        _dispatcher.BeginInvoke(() =>
        {
            if (state.ScreenPhase == PhoneScreenPhase.Off || state.DisplayId is null)
            {
                _screenshots.Dismiss();
                _catalogLoaded = false;
                _reorderRunningApps = true;
                _runningApps = Array.Empty<RunningApp>();
            }
            else if (state.ScreenPhase == PhoneScreenPhase.Ready && (state.ListVisible || state.Placed is null))
            {
                RefreshRunningApps();
            }
            SyncTouchRegion();
            SyncPhoneWindows(forceSyncUi: true);
            NotifySummary();
            UiStateChanged?.Invoke();
        });
    }

    private void OnScreenshotChanged(ScreenshotOverlay? overlay)
    {
        _dispatcher.BeginInvoke(() =>
        {
            SyncTouchRegion();
            SyncPhoneWindows(forceSyncUi: false);
            UiStateChanged?.Invoke();
        });
    }

    private void OnDeskGone(DeskGone gone)
    {
        _dispatcher.BeginInvoke(() =>
        {
            if (gone is DeskGone.App appGone)
                _runningApps = _runningApps.Where(a => a.Pid != appGone.Pid).ToList();
            else if (gone is DeskGone.Window winGone)
                _runningApps = _runningApps
                    .Select(a => a with { Windows = a.Windows.Where(w => w.Key != winGone.Key).ToList() })
                    .Where(a => a.Windows.Count > 0)
                    .ToList();
            UiStateChanged?.Invoke();
        });
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        _dispatcher.BeginInvoke(() => _desk.DisplaysChanged());
    }

    private RectD? _lastTouchRegionFrame;

    private void SyncTouchRegion()
    {
        var st = _desk.State;
        bool translating = _config.Gestures && _screenshots.Overlay?.IsCropping != true;
        _touchHook.RequireInjectedFlag = _config.GesturesInjectedOnly && !_sensor.ManualOverride;
        _touchHook.Options = new TouchOptions(
            AllowScroll: _config.GestureScroll,
            AllowRightClick: _config.GestureLongPressRightClick,
            NotifySelectionEnded: _config.CopyAfterSelection && !st.ListVisible);

        if (!translating || st.ScreenPhase != PhoneScreenPhase.Ready || st.DisplayId is not { } id || _displays.BoundsOf(id) is not { } bounds)
        {
            _lastTouchRegionFrame = null;
            _touchHook.SetRegion(null);
            return;
        }
        if (_lastTouchRegionFrame != bounds)
        {
            _lastTouchRegionFrame = bounds;
            Log.Info($"手势区域更新：{bounds.Short}");
        }
        var closeBtn = st.ListVisible ? null : st.CloseButton;
        _touchHook.SetRegion(TouchRegion.ForPhoneScreen(bounds, closeBtn));
    }

    private void ShowCopySelectionBubble(PointD selectionEnd)
    {
        var st = _desk.State;
        if (!_config.CopyAfterSelection || st.ListVisible || st.ScreenPhase != PhoneScreenPhase.Ready
            || st.DisplayId is not { } id || _displays.BoundsOf(id) is not { } bounds)
            return;

        _copyBubbleWindow ??= new CopySelectionBubbleWindow(this);
        int logicalW = _displays.WidthOf(id) ?? ScreenFit.DesignWidth;
        double scale = ScreenFit.Scale(logicalW);
        double dpiRatio = Math.Max(1.0, bounds.Width / Math.Max(1.0, logicalW));
        _copyBubbleWindow.ShowAtSelection(selectionEnd, bounds, scale, dpiRatio);
    }

    public async Task CopySelectionAtAsync(PointD point)
    {
        await _windows.CopySelectionAtAsync(point);
    }

    public void SyncSubPageActivation(bool focus)
    {
        if (_phoneHomeWindow is null) return;
        var st = _desk.State;
        if (st.ScreenPhase != PhoneScreenPhase.Ready || st.DisplayId is not { } id || _displays.BoundsOf(id) is not { } bounds)
            return;
        _phoneHomeWindow.ShowOnDisplay(bounds, focusOnShow: focus);
    }

    private void SyncPhoneWindows(bool forceSyncUi)
    {
        var st = _desk.State;
        var overlay = _screenshots.Overlay;
        if (st.ScreenPhase != PhoneScreenPhase.Ready || st.DisplayId is not { } id || _displays.BoundsOf(id) is not { } bounds)
        {
            _wasListShowing = false;
            _lastSyncedPhoneState = null;
            _lastSyncedBounds = null;
            _lastSyncedOverlay = null;
            _phoneHomeWindow?.CloseSubPage();
            _phoneHomeWindow?.Hide();
            _homeBarWindow?.Hide();
            HideAndDisposeScreenshotWindows();
            return;
        }

        bool changed = forceSyncUi
                       || !Equals(st, _lastSyncedPhoneState)
                       || !Equals(bounds, _lastSyncedBounds)
                       || !Equals(overlay, _lastSyncedOverlay)
                       || _config.Screenshot != _lastSyncedScreenshotSetting;
        if (!changed) return;

        _lastSyncedPhoneState = st;
        _lastSyncedBounds = bounds;
        _lastSyncedOverlay = overlay;
        _lastSyncedScreenshotSetting = _config.Screenshot;

        bool showList = st.ListVisible || st.Placed is null;
        if (showList != _wasListShowing)
        {
            _wasListShowing = showList;
            if (showList) _reorderRunningApps = true;
        }

        if (showList)
        {
            _homeBarWindow?.Hide();
            _phoneHomeWindow ??= new PhoneHomeWindow(this);
            _phoneHomeWindow.ShowOnDisplay(bounds, focusOnShow: false);
            _phoneHomeWindow.SyncState();
        }
        else
        {
            _phoneHomeWindow?.CloseSubPage();
            _phoneHomeWindow?.Hide();
            if (overlay?.IsCropping == true)
            {
                _homeBarWindow?.Hide();
            }
            else if (_displays.HomeBarAreaOf(id) is { } barArea)
            {
                _homeBarWindow ??= new HomeBarWindow(this);
                double scale = ScreenFit.Scale(_displays.WidthOf(id) ?? ScreenFit.DesignWidth);
                _homeBarWindow.ShowAtBounds(barArea, scale, _config.Screenshot);
            }
        }

        SyncScreenshotWindows(id, bounds, showList, overlay);
    }

    private void SyncScreenshotWindows(uint displayId, RectD bounds, bool showList, ScreenshotOverlay? overlay)
    {
        if (showList || overlay is null || overlay.IsCropping)
        {
            _screenshotOverlayWindow?.Close();
            _screenshotOverlayWindow = null;
        }
        else if (_displays.HomeBarAreaOf(displayId) is { } barArea)
        {
            _screenshotOverlayWindow ??= new ScreenshotOverlayWindow(this);
            int logicalW = _displays.WidthOf(displayId) ?? ScreenFit.DesignWidth;
            double scale = ScreenFit.Scale(logicalW);
            double dpiRatio = Math.Max(1.0, bounds.Width / Math.Max(1.0, logicalW));
            _screenshotOverlayWindow.UpdateAndShow(overlay, barArea, scale, dpiRatio);
        }

        if (overlay is ScreenshotOverlay.Cropping crop)
        {
            if (_cropWindow is null || !Equals(_cropWindowImage, crop.Image))
            {
                _cropWindow?.Close();
                _cropWindowImage = crop.Image;
                _cropWindow = new CropWindow(this, crop.Image);
            }
            _cropWindow.ShowOnDisplay(bounds);
        }
        else
        {
            _cropWindow?.Close();
            _cropWindow = null;
            _cropWindowImage = null;
        }
    }

    private void HideAndDisposeScreenshotWindows()
    {
        _copyBubbleWindow?.HideBubble();
        _screenshotOverlayWindow?.Close();
        _screenshotOverlayWindow = null;
        _cropWindow?.Close();
        _cropWindow = null;
        _cropWindowImage = null;
    }

    public void SetPhoneScreenEnabled(bool enabled)
    {
        _config = _config with { PhoneScreen = enabled };
        _configStore.Save(_config);
        OnTick();
        UiStateChanged?.Invoke();
    }

    public void SetGesturesEnabled(bool enabled)
    {
        _config = _config with { Gestures = enabled };
        _configStore.Save(_config);
        SyncTouchRegion();
        UiStateChanged?.Invoke();
    }

    public void SetGestureScrollEnabled(bool enabled)
    {
        _config = _config with { GestureScroll = enabled };
        _configStore.Save(_config);
        SyncTouchRegion();
        UiStateChanged?.Invoke();
    }

    public void SetGestureLongPressRightClickEnabled(bool enabled)
    {
        _config = _config with { GestureLongPressRightClick = enabled };
        _configStore.Save(_config);
        SyncTouchRegion();
        UiStateChanged?.Invoke();
    }

    public void SetCopyAfterSelectionEnabled(bool enabled)
    {
        _config = _config with { CopyAfterSelection = enabled };
        _configStore.Save(_config);
        if (!enabled) _copyBubbleWindow?.HideBubble();
        SyncTouchRegion();
        UiStateChanged?.Invoke();
    }

    public void SetGesturesInjectedOnlyEnabled(bool enabled)
    {
        _config = _config with { GesturesInjectedOnly = enabled };
        _configStore.Save(_config);
        SyncTouchRegion();
        UiStateChanged?.Invoke();
    }

    public void SetScreenshotEnabled(bool enabled)
    {
        _config = _config with { Screenshot = enabled };
        _configStore.Save(_config);
        if (!enabled) _screenshots.Dismiss();
        SyncPhoneWindows(forceSyncUi: true);
        UiStateChanged?.Invoke();
    }

    public void SetDefaultZoomSteps(int steps)
    {
        _config = _config with { ZoomSteps = steps.Clamp(AppConfig.MinZoomSteps, AppConfig.MaxZoomSteps) };
        _configStore.Save(_config);
        _desk.SetDefaultZoom(_config.ZoomSteps);
        UiStateChanged?.Invoke();
    }

    public void SelectPhone(PhoneChoice choice)
    {
        var resolved = Catalog.Resolve(choice);
        _config = _config with { Phone = resolved };
        _configStore.Save(_config);
        _desk.SetFit(ScreenFit.FromPhone(resolved.Screen));
        UiStateChanged?.Invoke();
    }

    public void SetManualOverride(bool on)
    {
        _sensor.ManualOverride = on;
        OnTick();
        UiStateChanged?.Invoke();
    }

    public void PullWindow(WindowInfo w) => _desk.Pull(w);

    public void OpenApp(AppInfo app)
    {
        switch (AppList.ActionFor(app, _runningApps))
        {
            case AppAction.Pull p:
                _desk.Pull(p.Window);
                break;
            case AppAction.Open o:
                _desk.Open(o.App);
                break;
        }
    }
    public void NewWindow(RunningApp app) => _desk.NewWindow(app);
    public void QuitApp(RunningApp app) => _desk.Quit(app);
    public void ForceQuitApp(int pid, string appName) => _desk.ForceQuit(pid, appName);
    public void CloseWindow(WindowInfo w) => _desk.Close(w);
    public void ShowList() => _desk.ShowList();
    public void ReturnToPlaced() => _desk.ReturnToPlaced();
    public void PutBack() => _desk.PutBack();
    public void ZoomBy(int delta) => _desk.Zoom(delta);
    public void DismissNotice() => _desk.DismissNotice();
    public void DismissScreen() => _desk.DismissScreen();

    public void TakeScreenshot()
    {
        if (_desk.State.Placed is not { } placed) return;
        _ = _screenshots.TakeAsync(placed.Window);
    }

    public void OpenCrop() => _screenshots.OpenCrop();
    public void CancelCrop() => _screenshots.CancelCrop();
    public void FinishCrop(RectD selection) => _screenshots.FinishCrop(selection);

    public IReadOnlyList<string> GetRecentLogs()
    {
        lock (_logGate) return _recentLogs.ToList();
    }

    public string GetStatusSummary()
    {
        if (!IsDriverPipeAvailable) return "未检测到虚拟屏驱动";
        return _desk.State.ScreenPhase switch
        {
            PhoneScreenPhase.Ready => _session.IsConnected ? "远程中（手机屏已就绪）" : "手机屏就绪",
            PhoneScreenPhase.Preparing => "正在创建手机屏...",
            PhoneScreenPhase.Failed => "建屏失败（重试中）",
            _ => _session.IsConnected ? "远程中" : "待机中"
        };
    }

    private void NotifySummary() => StateSummaryChanged?.Invoke(GetStatusSummary());

    private static PhoneCatalog LoadPhoneCatalog()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Assets", "phone-models.json");
        if (!File.Exists(path)) return PhoneCatalog.Empty;
        try
        {
            return PhoneCatalog.LoadFromFile(path);
        }
        catch (Exception ex)
        {
            Log.Error($"加载机型库失败：{ex.Message}");
            return PhoneCatalog.Empty;
        }
    }

    public void Write(LogLevel level, string message, DateTimeOffset timestamp)
    {
        string line = $"[{timestamp:HH:mm:ss}] [{level}] {message}";
        lock (_logGate)
        {
            _recentLogs.Add(line);
            if (_recentLogs.Count > 300)
                _recentLogs.RemoveRange(0, _recentLogs.Count - 300);
            try
            {
                string logFile = Path.Combine(_paths.LogDirectory, $"snug-{timestamp:yyyyMMdd}.log");
                File.AppendAllText(logFile, line + Environment.NewLine);
            }
            catch { }
        }
        LogAppended?.Invoke(line);
    }

    public async Task ShutdownAsync()
    {
        _shuttingDown = true;
        _tickTimer.Stop();
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        _touchHook.Dispose();
        await _desk.ShutdownAsync();
        await _displays.DisposeAsync();
        HideAndDisposeScreenshotWindows();
        _copyBubbleWindow?.Close();
        _phoneHomeWindow?.Close();
        _homeBarWindow?.Close();
    }
}
