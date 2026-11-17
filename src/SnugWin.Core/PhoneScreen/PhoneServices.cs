using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace SnugWin.Core.PhoneScreen;

public sealed record WindowLook(RectD Frame, bool FullScreen = false, bool Minimized = false);

public interface IPhoneDisplays
{
    Task<uint> CreateAsync(ScreenFit fit, CancellationToken ct = default);
    Task<bool> ReshapeAsync(uint displayId, ScreenFit fit, CancellationToken ct = default);
    ScreenFit? FitOf(uint displayId);
    Task<bool> RetireAsync(uint displayId, CancellationToken ct = default);
    bool IsOnline(uint displayId);
    int? WidthOf(uint displayId);
    Task SetWidthAsync(uint displayId, int width, CancellationToken ct = default);
    RectD? BoundsOf(uint displayId);
    RectD? WindowAreaOf(uint displayId);
    RectD? MainArea();
    IReadOnlyList<OtherDisplay> OtherDisplays();
    Task SetModeAsync(uint displayId, DisplayModeInfo mode, bool permanently, CancellationToken ct = default);
}

public interface IWindowControl
{
    Task<bool> ExistsAsync(WindowKey window);
    Task<bool> IsShownAsync(WindowKey window);
    Task<WindowLook?> LookAsync(WindowKey window);
    Task<RectD?> MoveAsync(WindowKey window, RectD frame);
    Task SetFullScreenAsync(WindowKey window, bool on);
    Task SetMinimizedAsync(WindowKey window, bool on);
    Task FocusAsync(WindowKey window);
    Task<bool> ZoomAsync(WindowKey window, int steps);
    Task<IReadOnlyList<WindowInfo>> ShownWindowsAsync(int pid);
    Task<WindowInfo?> AskingWindowAsync(int pid);
    Task<bool> CloseAsync(WindowKey window);
    Task<RectD?> CloseButtonAsync(WindowKey window);
    Task<Dictionary<WindowKey, RectD>> FramesAsync(int pid);
    Task<Dictionary<WindowKey, RectD>> SnapshotAsync();
    Task RestoreAsync(IReadOnlyDictionary<WindowKey, RectD> snapshot, WindowKey? except);
}

public interface IAppControl
{
    Task<int?> PidOfAsync(AppInfo app);
    Task<IReadOnlyList<int>> PidsAsync(string appId);
    Task<int?> OpenAsync(AppInfo app);
    Task<bool> NewWindowAsync(int pid);
    Task<bool> QuitAsync(int pid);
    Task<bool> ForceQuitAsync(int pid);
    Task<bool> IsRunningAsync(int pid);
    Task<IReadOnlyList<WindowInfo>> AppWindowsAsync(AppInfo app, int? launchedPid = null) =>
        Task.FromResult<IReadOnlyList<WindowInfo>>(Array.Empty<WindowInfo>());
}

public interface ITimeline
{
    double Now { get; }
    Task SleepAsync(double seconds, CancellationToken ct = default);
}

public sealed class SystemTimeline : ITimeline
{
    private readonly long _start = Stopwatch.GetTimestamp();

    public double Now => Stopwatch.GetElapsedTime(_start).TotalSeconds;

    public async Task SleepAsync(double seconds, CancellationToken ct = default)
    {
        if (seconds <= 0) return;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(seconds), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // ignore cancellation
        }
    }
}

public enum PhoneScreenErrorKind
{
    CreateFailed,
    NeverOnline
}

public sealed class PhoneScreenException : Exception
{
    public PhoneScreenErrorKind Kind { get; }

    public PhoneScreenException(PhoneScreenErrorKind kind, string? detail = null)
        : base(detail ?? (kind == PhoneScreenErrorKind.CreateFailed
            ? "虚拟显示器创建失败，请检查 virtual-display-rs 驱动是否已安装并运行"
            : "手机屏已创建但系统未将其上线"))
    {
        Kind = kind;
    }
}
