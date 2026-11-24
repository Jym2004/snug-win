using System.Collections.Generic;
using System.Linq;
using SnugWin.Core.Config;

namespace SnugWin.Core.PhoneScreen;

public enum ScreenBehavior
{
    TouchScreen,
    Computer
}

/// <summary>Volatile, user-selected remote source mode; intentionally not persisted.</summary>
public enum ManualRemoteScreenMode
{
    /// <summary>Automatically follow a confirmed RustDesk remote-desktop card.</summary>
    Auto = 3,
    Off = 0,
    TouchScreen = 1,
    Computer = 2
}

public sealed record ScreenWish(
    string Id,
    string Name,
    PhoneChoice Device,
    ScreenOrientation Orientation,
    ScreenFit Fit,
    ScreenBehavior Behavior,
    bool IsMain = false)
{
    public bool IsTouch => Behavior == ScreenBehavior.TouchScreen;
}

public static class ScreenPlan
{
    public static ScreenBehavior BehaviorOf(DeviceKind kind) =>
        kind.IsTouch() ? ScreenBehavior.TouchScreen : ScreenBehavior.Computer;

    public static ScreenFit FitOf(ScreenConfig screen) =>
        screen is { FixedNativePixels: true, Device.Kind: DeviceKind.Phone, Orientation: ScreenOrientation.Portrait }
            ? ScreenFit.FromNativePhone(screen.Device.Screen)
            : ScreenFit.FromDevice(screen.Device.Screen, screen.Device.Kind, screen.Orientation);

    /// <summary>
    /// Keep only the screens that suit the current controller during a remote session.
    /// Exempts persistent main screens (IsMain) and non-remote standby so v1.3.1 main screen
    /// is never dropped.
    /// </summary>
    public static IReadOnlyList<ScreenWish> ForController(IReadOnlyList<ScreenWish> wishes, bool wantsTouch, bool remote = true)
    {
        if (!remote || wishes.Count == 0) return wishes;
        return wishes.Where(w => w.IsMain || w.IsTouch == wantsTouch).ToList();
    }

    public static IReadOnlyList<ScreenWish> ForManualMode(
        IReadOnlyList<ScreenWish> wishes,
        ManualRemoteScreenMode mode)
    {
        if (!IsManualOverride(mode) || wishes.Count == 0) return [];
        bool wantsTouch = mode == ManualRemoteScreenMode.TouchScreen;
        return wishes.Where(w => w.IsMain || w.IsTouch == wantsTouch).ToList();
    }

    /// <summary>Combines independent screen requests without dropping the persistent standby wishes.</summary>
    public static IReadOnlyList<ScreenWish> MergeWishes(params IReadOnlyList<ScreenWish>[] sources) =>
        sources.SelectMany(source => source)
            .GroupBy(wish => wish.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();

    public static bool HasManualModeTarget(
        IReadOnlyList<ScreenWish> wishes,
        ManualRemoteScreenMode mode) => mode switch
        {
            ManualRemoteScreenMode.Auto => wishes.Any(wish => wish.IsTouch),
            ManualRemoteScreenMode.Off => false,
            ManualRemoteScreenMode.TouchScreen => wishes.Any(wish => wish.IsTouch),
            ManualRemoteScreenMode.Computer => wishes.Any(wish => !wish.IsTouch),
            _ => false
        };

    public static bool HasAutomaticTarget(IReadOnlyList<ScreenWish> wishes) =>
        wishes.Any(wish => wish.IsTouch);

    public static ManualRemoteScreenMode EffectiveRemoteMode(
        ManualRemoteScreenMode selectedMode,
        bool rustDeskConnected) => selectedMode switch
        {
            ManualRemoteScreenMode.Auto => rustDeskConnected
                ? ManualRemoteScreenMode.TouchScreen
                : ManualRemoteScreenMode.Off,
            _ => selectedMode
        };

    public static bool IsManualOverride(ManualRemoteScreenMode mode) =>
        mode is ManualRemoteScreenMode.TouchScreen or ManualRemoteScreenMode.Computer;

    public static string RustDeskModeLabel(ManualRemoteScreenMode selectedMode, bool rustDeskConnected) => selectedMode switch
    {
        ManualRemoteScreenMode.Auto when rustDeskConnected => "RustDesk：自动（已连接）",
        ManualRemoteScreenMode.Auto => "RustDesk：自动",
        ManualRemoteScreenMode.TouchScreen => "RustDesk：手机触控屏",
        ManualRemoteScreenMode.Computer => "RustDesk：电脑副屏",
        _ => "RustDesk：关闭"
    };

    public static IReadOnlyList<ScreenWish> DesiredWishes(
        AppConfig config,
        bool uuRemoteActive,
        bool uuWantsTouch,
        ManualRemoteScreenMode manualMode)
    {
        var configured = Wishes(config, remote: true);
        var standby = Wishes(config, remote: false);
        var uu = uuRemoteActive ? ForController(configured, uuWantsTouch) : [];
        var manual = ForManualMode(configured, manualMode);
        return MergeWishes(standby, uu, manual);
    }

    public static IReadOnlyList<ScreenWish> DesiredWishes(
        AppConfig config,
        bool uuRemoteActive,
        bool uuWantsTouch,
        ManualRemoteScreenMode selectedMode,
        bool rustDeskConnected)
    {
        ManualRemoteScreenMode effectiveMode = EffectiveRemoteMode(selectedMode, rustDeskConnected);
        if (!HasManualModeTarget(Wishes(config, remote: true), effectiveMode))
            effectiveMode = ManualRemoteScreenMode.Off;
        return DesiredWishes(config, uuRemoteActive, uuWantsTouch, effectiveMode);
    }

    public static bool IsRemoteActive(bool uuRemoteActive, ManualRemoteScreenMode manualMode) =>
        uuRemoteActive || IsManualOverride(manualMode);

    public static bool IsRemoteActive(bool uuRemoteActive, ManualRemoteScreenMode selectedMode, bool rustDeskConnected) =>
        IsRemoteActive(uuRemoteActive, EffectiveRemoteMode(selectedMode, rustDeskConnected));

    public static string? ManualModeStatus(ManualRemoteScreenMode mode, bool hasTarget) =>
        ManualModeStatus(mode, hasTarget, rustDeskConnected: false);

    public static string? ManualModeStatus(ManualRemoteScreenMode selectedMode, bool hasTarget, bool rustDeskConnected)
    {
        ManualRemoteScreenMode mode = EffectiveRemoteMode(selectedMode, rustDeskConnected);
        if (selectedMode == ManualRemoteScreenMode.Auto && !rustDeskConnected)
            return "RustDesk：自动检测中";
        if (mode == ManualRemoteScreenMode.Off) return null;
        string targetName = mode == ManualRemoteScreenMode.Computer ? "电脑副屏" : "手机触控屏";
        string prefix = selectedMode == ManualRemoteScreenMode.Auto ? "RustDesk 自动" : "RustDesk 手动模式";
        return hasTarget
            ? $"{prefix}（{targetName}）"
            : $"RustDesk 未找到已启用的{targetName}配置";
    }

    public static bool CanReenterPhoneScreen(
        bool uuEpisodeActive,
        bool uuManualOverride,
        ManualRemoteScreenMode manualMode,
        bool hasDismissedTouch) =>
        (uuEpisodeActive || uuManualOverride || manualMode == ManualRemoteScreenMode.TouchScreen)
        && hasDismissedTouch;

    public static IReadOnlyList<ScreenWish> Wishes(AppConfig config, bool remote, bool preview = false)
    {
        var enabled = config.EffectiveScreens()
            .Where(s => s.Enabled)
            .Take(ScreenConfig.Limit);

        return enabled
            .Where(s => remote || preview || string.Equals(s.Id, config.MainScreen, StringComparison.OrdinalIgnoreCase))
            .Select(s => new ScreenWish(
                s.Id,
                s.Name,
                s.Device,
                s.Orientation,
                FitOf(s),
                BehaviorOf(s.Device.Kind),
                IsMain: string.Equals(s.Id, config.MainScreen, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }
}
