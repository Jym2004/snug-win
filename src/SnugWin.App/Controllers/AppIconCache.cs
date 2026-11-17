using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SnugWin.Core.PhoneScreen;
using SnugWin.Windows.Native;

namespace SnugWin.App.Controllers;

public sealed class AppIconCache
{
    private const int ShellIconSize = 128;
    private const int SiigbfBiggerSizeOk = 0x01;
    private const int SiigbfIconOnly = 0x04;
    private static readonly Guid ShellItemImageFactoryGuid = new("bcc18b79-ba16-442f-80c4-8a59c30c463b");

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

        string? exePath = app.ExecutablePath;
        if (string.IsNullOrEmpty(exePath) && pid is { } p)
            exePath = Win32.GetProcessImagePath(p);

        string? shellTarget = !string.IsNullOrEmpty(app.LaunchTarget) && app.LaunchTarget.StartsWith("shell:", StringComparison.OrdinalIgnoreCase)
            ? app.LaunchTarget
            : exePath;

        ImageSource resolved = LoadShellIcon(shellTarget) ?? LoadExeIcon(exePath) ?? _fallback;
        _cache.TryAdd(key, resolved);
        return resolved;
    }

    public ImageSource GetIcon(WindowInfo window)
    {
        if (!string.IsNullOrEmpty(window.AppId) && _cache.TryGetValue(window.AppId, out var existing))
            return existing;

        string? path = Win32.GetProcessImagePath(window.Key.Pid);
        ImageSource resolved = LoadShellIcon(path) ?? LoadExeIcon(path) ?? _fallback;
        if (!string.IsNullOrEmpty(window.AppId))
            _cache.TryAdd(window.AppId, resolved);
        return resolved;
    }

    private static ImageSource? LoadShellIcon(string? parsingPath)
    {
        if (string.IsNullOrWhiteSpace(parsingPath))
            return null;

        Win32.IShellItemImageFactory? factory = null;
        IntPtr hBmp = IntPtr.Zero;
        try
        {
            int hr = Win32.SHCreateItemFromParsingName(parsingPath, IntPtr.Zero, ShellItemImageFactoryGuid, out factory);
            if (hr != 0 || factory is null)
                return null;

            hr = factory.GetImage(
                new Win32.SIZE { cx = ShellIconSize, cy = ShellIconSize },
                SiigbfBiggerSizeOk | SiigbfIconOnly,
                out hBmp);
            if (hr != 0 || hBmp == IntPtr.Zero)
                return null;

            return ConvertHBitmapPbgra32(hBmp);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (hBmp != IntPtr.Zero) Win32.DeleteObject(hBmp);
            if (factory is not null) Marshal.ReleaseComObject(factory);
        }
    }

    private static BitmapSource? ConvertHBitmapPbgra32(IntPtr hBmp)
    {
        if (Win32.GetObjectW(hBmp, Marshal.SizeOf<Win32.BITMAP>(), out var bm) == 0)
            return null;

        int w = bm.bmWidth;
        int h = bm.bmHeight;
        if (w <= 0 || h <= 0) return null;

        var bi = new Win32.BITMAPINFO
        {
            bmiHeader = new Win32.BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<Win32.BITMAPINFOHEADER>(),
                biWidth = w,
                biHeight = -h, // top-down DIB
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0 // BI_RGB
            }
        };

        int stride = w * 4;
        byte[] pixels = new byte[stride * h];
        IntPtr dc = Win32.GetDC(IntPtr.Zero);
        try
        {
            if (Win32.GetDIBits(dc, hBmp, 0, (uint)h, pixels, ref bi, 0) == 0)
                return null;
        }
        finally
        {
            Win32.ReleaseDC(IntPtr.Zero, dc);
        }

        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, pixels, stride);
        bmp.Freeze();
        return bmp;
    }

    private static ImageSource? LoadExeIcon(string? exePath)
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
                BitmapSizeOptions.FromWidthAndHeight(64, 64));
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
                new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x60, 0xA5, 0xFA)),
                null,
                new Rect(18, 18, 28, 28),
                7,
                7);
        }
        var img = new DrawingImage(group);
        img.Freeze();
        return img;
    }
}
