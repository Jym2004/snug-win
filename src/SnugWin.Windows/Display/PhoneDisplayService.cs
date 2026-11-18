using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SnugWin.Core.Persistence;
using SnugWin.Core.PhoneScreen;
using SnugWin.Core.Support;

namespace SnugWin.Windows.Display;

public sealed class PhoneDisplayService : IPhoneDisplays, IAsyncDisposable
{
    private static readonly TimeSpan OnlineTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan OfflineTimeout = TimeSpan.FromSeconds(10);

    private readonly IVirtualDisplayIpc _ipc;
    private readonly OwnedDisplayStore? _ownedStore;
    private readonly Func<bool> _isPipeAvailable;
    private readonly Func<IReadOnlyList<MonitorSnapshot>> _getActiveMonitors;
    private readonly Func<int, int, int, int, bool> _tryAttachVirtual;
    private readonly TimeSpan _onlineTimeout;
    private readonly Dictionary<uint, ScreenFit> _fits = new();
    private readonly Dictionary<uint, string> _deviceNames = new();

    public PhoneDisplayService(VirtualDisplayIpcClient? ipc = null, SnugPaths? paths = null)
        : this(
            (IVirtualDisplayIpc?)ipc ?? new VirtualDisplayIpcClient(),
            paths,
            () => VirtualDisplayIpcClient.IsDriverPipeAvailable(),
            () => DisplayConfigService.GetActiveMonitors(),
            (w, h, x, y) => DisplayConfigService.TryAttachVirtualDisplay(w, h, x, y, out _),
            OnlineTimeout)
    {
    }

    internal PhoneDisplayService(
        IVirtualDisplayIpc ipc,
        SnugPaths? paths,
        Func<bool> isPipeAvailable,
        Func<IReadOnlyList<MonitorSnapshot>> getActiveMonitors,
        Func<int, int, int, int, bool>? tryAttachVirtual = null,
        TimeSpan? onlineTimeout = null)
    {
        _ipc = ipc;
        if (paths is not null)
            _ownedStore = new OwnedDisplayStore(paths.OwnedDisplaysFile);
        _isPipeAvailable = isPipeAvailable;
        _getActiveMonitors = getActiveMonitors;
        _tryAttachVirtual = tryAttachVirtual ?? ((w, h, x, y) => DisplayConfigService.TryAttachVirtualDisplay(w, h, x, y, out _));
        _onlineTimeout = onlineTimeout ?? OnlineTimeout;
    }

    public async Task CleanupOrphansAsync(CancellationToken ct = default)
    {
        if (_ownedStore is null) return;
        var ownedIds = _ownedStore.LoadIds();
        if (ownedIds.Count == 0) return;

        if (!_isPipeAvailable())
        {
            Log.Warn("启动清理时驱动管道暂未就绪，保留归属记录以便管道恢复后清理");
            return;
        }

        try
        {
            await _ipc.RemoveAsync(ownedIds.ToArray(), ct).ConfigureAwait(false);
            await Task.Delay(300, ct).ConfigureAwait(false);
            _ownedStore.ClearAll();
            Log.Info($"已按归属记录清理残留手机屏：#{string.Join(", #", ownedIds)}");
        }
        catch (Exception ex)
        {
            Log.Warn($"启动清理残留虚拟屏时异常：{ex.Message}");
        }
    }

    public Task<uint> CreateAsync(ScreenFit fit, CancellationToken ct = default) =>
        CreateAsync(fit, null, ct);

    public async Task<uint> CreateAsync(ScreenFit fit, string? displayName, CancellationToken ct = default)
    {
        if (!_isPipeAvailable())
        {
            Log.Warn("未检测到虚拟屏管道，尝试自动重启 Cherry Virtual Display 设备...");
            if (await TryRestartVirtualDisplayDeviceAsync(ct).ConfigureAwait(false))
            {
                Log.Info("虚拟屏设备重启成功，驱动管道已恢复");
            }
            else
            {
                throw new PhoneScreenException(
                    PhoneScreenErrorKind.CreateFailed,
                    "未检测到 virtual-display-rs 驱动管道（\\\\.\\pipe\\virtualdisplaydriver），请确认驱动已安装且正常运行");
            }
        }

        var beforeMonitors = _getActiveMonitors();
        var existingDevices = beforeMonitors
            .Select(m => m.DeviceName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        int existingVirtualCount = beforeMonitors.Count(m =>
            m.DeviceString.Contains("Virtual", StringComparison.OrdinalIgnoreCase)
            || m.FriendlyName.Contains("VirtuDisplay", StringComparison.OrdinalIgnoreCase)
            || m.FriendlyName.Contains("Virtual", StringComparison.OrdinalIgnoreCase));

        string vddName = string.IsNullOrWhiteSpace(displayName) ? PhoneScreenSpec.Name : displayName;
        var modes = BuildModes(fit);
        foreach (uint candidateId in CandidateIdsStartingAfter(existingVirtualCount))
        {
            if (_fits.ContainsKey(candidateId))
                continue;

            try
            {
                var mon = new VddMonitor(candidateId, vddName, true, modes);
                await _ipc.NotifyAsync([mon], ct).ConfigureAwait(false);
                Log.Info($"已向驱动发送建屏请求（{vddName} id={candidateId}，共 {modes.Count} 档，首档 {modes[0].Width}x{modes[0].Height}）");
            }
            catch (Exception ex)
            {
                Log.Warn($"手机屏（id={candidateId}）IPC Notify 失败：{ex.Message}");
                continue;
            }

            var (basePxW, basePxH) = fit.PixelSize(fit.BaseWidth);
            var snapshot = await WaitForOnlineAsync(existingDevices, basePxW, basePxH, _onlineTimeout, _getActiveMonitors, _tryAttachVirtual, ct).ConfigureAwait(false);
            if (snapshot is null)
            {
                Log.Warn($"手机屏（id={candidateId}）创建后 {_onlineTimeout.TotalSeconds:0.#}s 内未在 Windows 上线，跳过该候选 ID 而不移除未知设备");
                continue;
            }

            _fits[candidateId] = fit;
            _deviceNames[candidateId] = snapshot.DeviceName;
            _ownedStore?.RecordOwned(candidateId);

            await SetWidthAsync(candidateId, fit.BaseWidth, ct).ConfigureAwait(false);
            return candidateId;
        }

        throw new PhoneScreenException(PhoneScreenErrorKind.NeverOnline);
    }

    internal static IReadOnlyList<uint> CandidateIdsStartingAfter(int existingVirtualCount)
    {
        var ids = PhoneScreenSpec.CandidateIds;
        if (existingVirtualCount <= 0 || ids.Length <= 1)
            return ids;
        int offset = existingVirtualCount % ids.Length;
        if (offset == 0) return ids;
        var rotated = new uint[ids.Length];
        for (int i = 0; i < ids.Length; i++)
            rotated[i] = ids[(i + offset) % ids.Length];
        return rotated;
    }

    public async Task<bool> ReshapeAsync(uint displayId, ScreenFit fit, CancellationToken ct = default)
    {
        if (!_deviceNames.ContainsKey(displayId)) return false;

        var modes = BuildModes(fit);
        try
        {
            var mon = new VddMonitor(displayId, PhoneScreenSpec.Name, true, modes);
            if (_ipc.IsLegacyProtocol)
                await _ipc.ReplaceLegacyAsync([displayId], [mon], ct).ConfigureAwait(false);
            else
                await _ipc.NotifyAsync([mon], ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warn($"手机屏 #{displayId} 换比例 Notify 失败：{ex.Message}");
            return false;
        }

        _fits[displayId] = fit;
        var (basePxW, basePxH) = fit.PixelSize(fit.BaseWidth);
        string devName = _deviceNames[displayId];
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(4) && !ct.IsCancellationRequested)
        {
            if (DisplayConfigService.HasDisplayMode(devName, basePxW, basePxH))
                break;
            await Task.Delay(100, ct).ConfigureAwait(false);
        }

        await SetWidthAsync(displayId, fit.BaseWidth, ct).ConfigureAwait(false);
        Log.Info($"手机屏 #{displayId} 已更换比例为 {fit}");
        return true;
    }

    public ScreenFit? FitOf(uint displayId) =>
        _fits.TryGetValue(displayId, out var fit) ? fit : null;

    public async Task<bool> RetireAsync(uint displayId, CancellationToken ct = default)
    {
        _fits.Remove(displayId);
        _deviceNames.TryGetValue(displayId, out var devName);
        _deviceNames.Remove(displayId);

        try
        {
            await _ipc.RemoveAsync([displayId], ct).ConfigureAwait(false);
            _ownedStore?.ReleaseOwned(displayId);
        }
        catch (Exception ex)
        {
            Log.Warn($"移除手机屏 #{displayId} 失败：{ex.Message}");
        }

        if (string.IsNullOrEmpty(devName)) return true;

        var sw = Stopwatch.StartNew();
        bool gone = false;
        while (sw.Elapsed < OfflineTimeout && !ct.IsCancellationRequested)
        {
            if (!DisplayConfigService.GetActiveMonitors().Any(m => m.DeviceName.Equals(devName, StringComparison.OrdinalIgnoreCase)))
            {
                gone = true;
                break;
            }
            await Task.Delay(150, ct).ConfigureAwait(false);
        }
        if (gone) RestoreCursorIfStranded();
        return gone;
    }

    private static void RestoreCursorIfStranded()
    {
        if (!Native.Win32.GetCursorPos(out var pt)) return;
        var monitors = DisplayConfigService.GetActiveMonitors();
        if (monitors.Count == 0) return;
        bool onAny = monitors.Any(m => m.Bounds.Contains(new PointD(pt.X, pt.Y)));
        if (!onAny)
        {
            var primary = monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors[0];
            Native.Win32.SetCursorPos((int)Math.Round(primary.Bounds.MidX), (int)Math.Round(primary.Bounds.MidY));
        }
    }

    public bool IsOnline(uint displayId)
    {
        if (!_deviceNames.TryGetValue(displayId, out var devName)) return false;
        return DisplayConfigService.GetActiveMonitors()
            .Any(m => m.DeviceName.Equals(devName, StringComparison.OrdinalIgnoreCase));
    }

    public int? WidthOf(uint displayId)
    {
        var mon = FindMonitor(displayId);
        if (mon is null) return null;
        if (_fits.TryGetValue(displayId, out var fit))
        {
            foreach (int w in fit.Widths)
            {
                if (fit.PixelSize(w).PixelWidth == mon.Mode.Width)
                    return w;
            }
        }
        return (int)Math.Round(mon.Mode.Width / 2.0, MidpointRounding.AwayFromZero);
    }

    public async Task SetWidthAsync(uint displayId, int width, CancellationToken ct = default)
    {
        if (!_fits.TryGetValue(displayId, out var fit) || !_deviceNames.TryGetValue(displayId, out var devName))
            return;

        var (pxW, pxH) = fit.PixelSize(width);
        var current = FindMonitor(displayId);
        if (current is null || current.Mode.Width != pxW || current.Mode.Height != pxH)
        {
            if (!DisplayConfigService.SetResolution(devName, pxW, pxH, 60))
                return;
            await Task.Delay(150, ct).ConfigureAwait(false);
        }

        var after = FindMonitor(displayId);
        int targetDpi = fit.TargetDpiPercent(width);
        if (after is not null && after.Mode.DpiScalePercent != targetDpi)
        {
            if (DisplayConfigService.SetDpiScalePercent(after.AdapterId, after.SourceId, targetDpi))
            {
                var updated = FindMonitor(displayId);
                Log.Info($"虚拟屏 #{displayId} 切到 {pxW}x{pxH}，DPI 设为 {updated?.Mode.DpiScalePercent ?? targetDpi}%");
            }
        }
    }

    public RectD? BoundsOf(uint displayId) => FindMonitor(displayId)?.Bounds;

    public RectD? WindowAreaOf(uint displayId)
    {
        var mon = FindMonitor(displayId);
        if (mon is null) return null;
        var work = mon.WorkArea;
        if (_fits.TryGetValue(displayId, out var fit) && fit.Kind == DeviceKind.Computer)
            return work;

        int logicalW = WidthOf(displayId) ?? ScreenFit.DesignWidth;
        var logicalSize = fit?.Size(logicalW) ?? new SizeD(logicalW, logicalW);
        double scale = Math.Max(1.0, mon.Mode.Width / (double)logicalW);
        double homeBarPx = Math.Round(ScreenFit.HomeBarHeight(logicalSize) * scale);
        double h = Math.Max(100, work.Height - homeBarPx);
        return new RectD(work.X, work.Y, work.Width, h);
    }

    public RectD? HomeBarAreaOf(uint displayId)
    {
        if (_fits.TryGetValue(displayId, out var fit) && fit.Kind == DeviceKind.Computer)
            return null;
        var mon = FindMonitor(displayId);
        if (mon is null || WindowAreaOf(displayId) is not { } area) return null;
        var work = mon.WorkArea;
        double barH = Math.Max(28, work.MaxY - area.MaxY);
        return new RectD(work.X, area.MaxY, work.Width, barH);
    }

    public RectD? MainArea()
    {
        var all = DisplayConfigService.GetActiveMonitors();
        var phoneSet = _deviceNames.Values.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var main = all.FirstOrDefault(m => m.IsPrimary && !phoneSet.Contains(m.DeviceName))
                   ?? all.FirstOrDefault(m => !phoneSet.Contains(m.DeviceName));
        return main?.WorkArea;
    }

    public IReadOnlyList<OtherDisplay> OtherDisplays()
    {
        var phoneSet = _deviceNames.Values.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return DisplayConfigService.GetActiveMonitors()
            .Where(m => !phoneSet.Contains(m.DeviceName)
                        && !m.DeviceString.Contains("Virtual", StringComparison.OrdinalIgnoreCase)
                        && !m.FriendlyName.Contains("VirtuDisplay", StringComparison.OrdinalIgnoreCase))
            .Select(m => new OtherDisplay(m.StableId, m.DeviceName, m.FriendlyName, m.Mode))
            .ToList();
    }

    public Task SetModeAsync(uint displayId, DisplayModeInfo mode, bool permanently, CancellationToken ct = default)
    {
        var target = DisplayConfigService.GetActiveMonitors().FirstOrDefault(m => m.StableId == displayId);
        if (target is null) return Task.CompletedTask;
        if (target.Mode.Width != mode.Width || target.Mode.Height != mode.Height || Math.Abs(target.Mode.RefreshRate - mode.RefreshRate) >= 1.0)
            DisplayConfigService.SetResolution(target.DeviceName, mode.Width, mode.Height, mode.RefreshRate);
        if (target.Mode.DpiScalePercent != mode.DpiScalePercent)
            DisplayConfigService.SetDpiScalePercent(target.AdapterId, target.SourceId, mode.DpiScalePercent);
        return Task.CompletedTask;
    }

    private MonitorSnapshot? FindMonitor(uint displayId)
    {
        if (!_deviceNames.TryGetValue(displayId, out var devName)) return null;
        return DisplayConfigService.GetActiveMonitors()
            .FirstOrDefault(m => m.DeviceName.Equals(devName, StringComparison.OrdinalIgnoreCase));
    }

    private static List<VddMode> BuildModes(ScreenFit fit)
    {
        // Register widest resolution first so Windows / driver brings landscape & hi-res screens online cleanly
        var order = fit.RegistrationOrder;
        var list = new List<VddMode>(order.Count);
        foreach (int w in order)
        {
            var (pxW, pxH) = fit.PixelSize(w);
            list.Add(new VddMode((uint)pxW, (uint)pxH, [60]));
        }
        return list;
    }

    private static async Task<MonitorSnapshot?> WaitForOnlineAsync(
        HashSet<string> existingDevices,
        int basePxW,
        int basePxH,
        TimeSpan timeout,
        Func<IReadOnlyList<MonitorSnapshot>> getActiveMonitors,
        Func<int, int, int, int, bool> tryAttachVirtual,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout && !ct.IsCancellationRequested)
        {
            var all = getActiveMonitors();
            var newlyAdded = all.FirstOrDefault(m =>
                !existingDevices.Contains(m.DeviceName)
                && (m.DeviceString.Contains("Virtual", StringComparison.OrdinalIgnoreCase)
                    || m.FriendlyName.Contains("Virtual", StringComparison.OrdinalIgnoreCase)
                    || DisplayConfigService.HasDisplayMode(m.DeviceName, basePxW, basePxH)));

            if (newlyAdded is not null)
                return newlyAdded;

            var anyNew = all.FirstOrDefault(m => !existingDevices.Contains(m.DeviceName));
            if (anyNew is not null)
                return anyNew;

            int rightEdge = all.Count > 0 ? (int)all.Max(m => m.Bounds.MaxX) : 2560;
            if (tryAttachVirtual(basePxW, basePxH, rightEdge, 0))
            {
                await Task.Delay(250, ct).ConfigureAwait(false);
                var attached = getActiveMonitors().FirstOrDefault(m => !existingDevices.Contains(m.DeviceName));
                if (attached is not null)
                    return attached;
            }

            await Task.Delay(150, ct).ConfigureAwait(false);
        }
        return null;
    }

    private static async Task<bool> TryRestartVirtualDisplayDeviceAsync(CancellationToken ct)
    {
        try
        {
            string? instanceId = FindVirtualDisplayInstanceId();
            if (string.IsNullOrEmpty(instanceId))
            {
                Log.Warn("未在 ROOT\\DISPLAY 注册表中找到 VirtualDisplayDriver 设备");
                return false;
            }

            Log.Info($"正在执行 pnputil /restart-device \"{instanceId}\"...");
            var psi = new ProcessStartInfo
            {
                FileName = "pnputil.exe",
                Arguments = $"/restart-device \"{instanceId}\"",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var proc = Process.Start(psi);
            if (proc is null) return false;
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
            Log.Info($"pnputil /restart-device 退出码={proc.ExitCode}");

            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < TimeSpan.FromSeconds(5) && !ct.IsCancellationRequested)
            {
                if (VirtualDisplayIpcClient.IsDriverPipeAvailable())
                    return true;
                await Task.Delay(200, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"自动重启虚拟屏设备失败：{ex.Message}");
        }
        return false;
    }

    private static string? FindVirtualDisplayInstanceId()
    {
        try
        {
            using var root = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\ROOT\DISPLAY");
            if (root is null) return null;

            foreach (string sub in root.GetSubKeyNames())
            {
                using var dev = root.OpenSubKey(sub);
                if (dev is null) continue;
                var hwIds = dev.GetValue("HardwareID") as string[];
                if (hwIds is not null && hwIds.Any(id => id.Contains("VirtualDisplayDriver", StringComparison.OrdinalIgnoreCase)))
                    return $@"ROOT\DISPLAY\{sub}";
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"读取 ROOT\\DISPLAY 注册表失败：{ex.Message}");
        }
        return null;
    }

    public ValueTask DisposeAsync() => _ipc.DisposeAsync();
}
