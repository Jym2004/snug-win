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
        ScreenFit.FromDevice(screen.Device.Screen, screen.Device.Kind, screen.Orientation);

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
