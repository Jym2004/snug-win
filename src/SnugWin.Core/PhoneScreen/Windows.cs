using System;
using System.Collections.Generic;
using System.Linq;

namespace SnugWin.Core.PhoneScreen;

/// <summary>
/// Stable window identity across a Windows session: PID + HWND (32/64-bit pointer stored as ulong).
/// </summary>
public readonly record struct WindowKey(int Pid, ulong Handle)
{
    public override string ToString() => $"{Pid}/{Handle:X}";
}

public sealed record WindowInfo(
    WindowKey Key,
    string AppName,
    string? AppId,
    string Title,
    bool Minimized = false)
{
    public string Label => string.IsNullOrEmpty(Title)
        ? AppName
        : $"{AppName}「{(Title.Length > 30 ? Title[..30] : Title)}」";

    /// <summary>
    /// Strip invisible formatting characters (zero-width spaces, directional marks) except ZWJ.
    /// </summary>
    public static string VisibleTitle(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return string.Empty;
        Span<char> buf = stackalloc char[raw.Length];
        int len = 0;
        foreach (char c in raw)
        {
            if (c == '\u200D' || System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.Format)
            {
                buf[len++] = c;
            }
        }
        return new string(buf[..len]).Trim();
    }
}

public sealed record AppInfo(
    string AppId,
    string Name,
    string? ExecutablePath,
    string? LaunchTarget = null,
    string? Arguments = null,
    string? WorkingDirectory = null);

public sealed record RunningApp(
    AppInfo App,
    int Pid,
    IReadOnlyList<WindowInfo> Windows);

public sealed record DisplayModeInfo(
    int Width,
    int Height,
    double RefreshRate,
    int? DpiScalePercent)
{
    public bool IsSameAs(DisplayModeInfo other) =>
        Width == other.Width
        && Height == other.Height
        && Math.Abs(RefreshRate - other.RefreshRate) < 0.5
        && (DpiScalePercent is null || other.DpiScalePercent is null || DpiScalePercent == other.DpiScalePercent);

    public override string ToString() => $"{Width}x{Height}@{RefreshRate:0.#}Hz ({(DpiScalePercent is { } d ? $"{d}%" : "?%")})";
}

public sealed record OtherDisplay(
    uint DisplayId,
    string DeviceName,
    string FriendlyName,
    DisplayModeInfo Mode,
    bool Asleep = false);
