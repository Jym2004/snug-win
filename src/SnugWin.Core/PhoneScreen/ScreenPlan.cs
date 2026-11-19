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
    /// Keep only the screens that suit the current controller. Do not fall back to another
    /// screen kind: a computer must not receive a phone touch screen just because no computer
    /// screen was configured (and vice versa). Unknown controllers are already classified by
    /// the caller as touch-capable before reaching this method.
    /// </summary>
    public static IReadOnlyList<ScreenWish> ForController(IReadOnlyList<ScreenWish> wishes, bool wantsTouch)
    {
        if (wishes.Count == 0) return wishes;
        return wishes.Where(w => w.IsTouch == wantsTouch).ToList();
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
