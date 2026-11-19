using System.Collections.Generic;
using System.Linq;
using SnugWin.Core.Config;

namespace SnugWin.Core.PhoneScreen;

public enum ScreenBehavior
{
    TouchScreen,
    Computer
}

public sealed record ScreenWish(
    string Id,
    string Name,
    PhoneChoice Device,
    ScreenOrientation Orientation,
    ScreenFit Fit,
    ScreenBehavior Behavior)
{
    public bool IsTouch => Behavior == ScreenBehavior.TouchScreen;
}

public static class ScreenPlan
{
    public static ScreenBehavior BehaviorOf(DeviceKind kind) =>
        kind.IsTouch() ? ScreenBehavior.TouchScreen : ScreenBehavior.Computer;

    public static ScreenFit FitOf(ScreenConfig screen) =>
        ScreenFit.FromDevice(screen.Device.Screen, screen.Device.Kind, screen.Orientation);

    /// <summary>
    /// Keep only the screens that suit the current controller: touch devices (phone/tablet)
    /// get the touch screens, desktop controllers get the computer screens. When the config
    /// only holds one kind, keep it as-is so a single-screen setup never goes blank.
    /// </summary>
    public static IReadOnlyList<ScreenWish> ForController(IReadOnlyList<ScreenWish> wishes, bool wantsTouch)
    {
        if (wishes.Count == 0) return wishes;
        var matched = wishes.Where(w => w.IsTouch == wantsTouch).ToList();
        return matched.Count > 0 ? matched : wishes;
    }

    public static IReadOnlyList<ScreenWish> Wishes(AppConfig config, bool remote, bool preview = false)
    {
        if (!remote && !preview) return [];

        return config.EffectiveScreens()
            .Where(s => s.Enabled)
            .Take(ScreenConfig.Limit)
            .Select(s => new ScreenWish(
                s.Id,
                s.Name,
                s.Device,
                s.Orientation,
                FitOf(s),
                BehaviorOf(s.Device.Kind)))
            .ToList();
    }
}
