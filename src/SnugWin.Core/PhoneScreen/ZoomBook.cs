using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace SnugWin.Core.PhoneScreen;

public enum ZoomScope
{
    LiveWindow,
    RestoredWindow,
    Workspace,
    App
}

public sealed class ZoomBook
{
    public const int MinSteps = -4;
    public const int MaxSteps = 8;

    /// <summary>
    /// Windows apps supporting Ctrl+= / Ctrl+- zoom (matched by lowercase executable / app ID).
    /// </summary>
    public static readonly IReadOnlyDictionary<string, ZoomScope> ZoomableApps =
        new Dictionary<string, ZoomScope>(StringComparer.OrdinalIgnoreCase)
        {
            ["WindowsTerminal.exe"] = ZoomScope.LiveWindow,
            ["wt.exe"] = ZoomScope.LiveWindow,
            ["wezterm-gui.exe"] = ZoomScope.LiveWindow,
            ["alacritty.exe"] = ZoomScope.LiveWindow,
            ["mintty.exe"] = ZoomScope.LiveWindow,
            ["Code.exe"] = ZoomScope.Workspace,
            ["Code - Insiders.exe"] = ZoomScope.Workspace,
            ["Cursor.exe"] = ZoomScope.Workspace,
            ["Windsurf.exe"] = ZoomScope.Workspace,
            ["Zed.exe"] = ZoomScope.App,
        };

    [JsonPropertyName("remembered")]
    public Dictionary<string, int> Remembered { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public static bool IsZoomable(string? appId) => ScopeOf(appId) is not null;

    public static ZoomScope? ScopeOf(string? appId) =>
        appId is not null && ZoomableApps.TryGetValue(appId, out var scope) ? scope : null;

    public int StepsFor(string? appId, int defaultSteps)
    {
        if (appId is null || !IsZoomable(appId)) return 0;
        int raw = Remembered.TryGetValue(appId, out int v) ? v : defaultSteps;
        return raw.Clamp(MinSteps, MaxSteps);
    }

    public void Remember(int steps, string appId)
    {
        Remembered[appId] = steps.Clamp(MinSteps, MaxSteps);
    }

    public void ForgetAll() => Remembered.Clear();
}
