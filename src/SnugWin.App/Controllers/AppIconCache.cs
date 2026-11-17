using System;
using System.Collections.Concurrent;
using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SnugWin.Core.PhoneScreen;
using SnugWin.Windows.Native;

namespace SnugWin.App.Controllers;

public sealed class AppIconCache
{
    private readonly ConcurrentDictionary<string, ImageSource> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ImageSource _fallback = CreateFallbackIcon();

    public void Preload(IEnumerable<AppInfo> apps)
    {
        foreach (var app in apps)
            GetIcon(app);
    }

    public void Preload(IEnumerable<RunningApp> running)
    {
        foreach (var r in running)
            GetIcon(r.App, r.Pid);
    }

    public ImageSource GetIcon(AppInfo app, int? pid = null)
    {
        string key = app.AppId;
        if (_cache.TryGetValue(key, out var existing))
            return existing;

        string? path = app.ExecutablePath;
        if (string.IsNullOrEmpty(path) && pid is { } p)
            path = Win32.GetProcessImagePath(p);

        ImageSource resolved = LoadIcon(path) ?? _fallback;
        _cache.TryAdd(key, resolved);
        return resolved;
    }

    public ImageSource GetIcon(WindowInfo window)
    {
        if (!string.IsNullOrEmpty(window.AppId) && _cache.TryGetValue(window.AppId, out var existing))
            return existing;

        string? path = Win32.GetProcessImagePath(window.Key.Pid);
        ImageSource resolved = LoadIcon(path) ?? _fallback;
        if (!string.IsNullOrEmpty(window.AppId))
            _cache.TryAdd(window.AppId, resolved);
        return resolved;
    }

    private static ImageSource? LoadIcon(string? exePath)
    {
        if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
            return null;

        try
        {
            using var ico = Icon.ExtractAssociatedIcon(exePath);
            if (ico is null) return null;
            var bmp = Imaging.CreateBitmapSourceFromHIcon(
                ico.Handle,
                Int32Rect.Empty,
                BitmapSizeOptions.FromWidthAndHeight(48, 48));
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    private static ImageSource CreateFallbackIcon()
    {
        var group = new DrawingGroup();
        using (var ctx = group.Open())
        {
            ctx.DrawRoundedRectangle(
                new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2C, 0x2C, 0x2E)),
                null,
                new Rect(0, 0, 64, 64),
                14,
                14);
            ctx.DrawRoundedRectangle(
                new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x8E, 0x8E, 0x93)),
                null,
                new Rect(18, 18, 28, 28),
                6,
                6);
        }
        var img = new DrawingImage(group);
        img.Freeze();
        return img;
    }
}
