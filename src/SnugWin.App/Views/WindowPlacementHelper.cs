using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using SnugWin.Core.PhoneScreen;
using SnugWin.Windows.Native;

namespace SnugWin.App.Views;

internal static class WindowPlacementHelper
{
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_FRAMECHANGED = 0x0020;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private const int GWL_STYLE = -16;
    private const int GWL_EXSTYLE = -20;
    private const long WS_MINIMIZEBOX = 0x00020000L;
    private const long WS_MAXIMIZEBOX = 0x00010000L;
    private const long WS_SYSMENU = 0x00080000L;
    private const long WS_EX_TRANSPARENT = 0x00000020L;
    private const long WS_EX_NOACTIVATE = 0x08000000L;
    private const long WS_EX_TOOLWINDOW = 0x00000080L;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern long GetWindowLongPtrW(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern long SetWindowLongPtrW(IntPtr hWnd, int nIndex, long dwNewLong);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private static readonly IntPtr HWND_NOTOPMOST = new(-2);

    public static void PlacePhysical(
        Window window,
        RectD bounds,
        bool topmost,
        bool noActivate,
        bool focusIfActivated = false,
        bool passthrough = false)
    {
        var helper = new WindowInteropHelper(window);
        IntPtr hwnd = helper.EnsureHandle();

        long ex = GetWindowLongPtrW(hwnd, GWL_EXSTYLE);
        long desired = ex | WS_EX_TOOLWINDOW;
        if (noActivate)
            desired |= WS_EX_NOACTIVATE;
        else
            desired &= ~WS_EX_NOACTIVATE;

        if (passthrough)
            desired |= WS_EX_TRANSPARENT;
        else
            desired &= ~WS_EX_TRANSPARENT;

        bool styleChanged = desired != ex;
        if (styleChanged)
            SetWindowLongPtrW(hwnd, GWL_EXSTYLE, desired);

        int tx = (int)Math.Round(bounds.X);
        int ty = (int)Math.Round(bounds.Y);
        int tw = Math.Max(1, (int)Math.Round(bounds.Width));
        int th = Math.Max(1, (int)Math.Round(bounds.Height));

        bool wasVisible = window.IsVisible;
        if (!wasVisible)
        {
            window.ShowActivated = !noActivate;
            window.Show();
        }
        else if (!styleChanged && !focusIfActivated && Win32.GetWindowRect(hwnd, out var cur)
                 && cur.Left == tx && cur.Top == ty && cur.Width == tw && cur.Height == th)
        {
            // Already visible at exact physical bounds & style; avoid redundant SetWindowPos every tick.
            return;
        }

        uint flags = SWP_SHOWWINDOW | (noActivate ? SWP_NOACTIVATE : 0u);
        SetWindowPos(
            hwnd,
            topmost ? HWND_TOPMOST : HWND_NOTOPMOST,
            tx,
            ty,
            tw,
            th,
            flags);

        if (!noActivate && focusIfActivated)
            SetForegroundWindow(hwnd);
    }

    public static void ApplyModernDarkChrome(Window window) =>
        ApplyWindowChromeTheme(window, isDark: true);

    public static void ApplyWindowChromeTheme(Window window, bool isDark)
    {
        try
        {
            window.Icon = System.Windows.Media.Imaging.BitmapFrame.Create(
                new Uri("pack://application:,,,/Assets/snugwin.ico"));
        }
        catch
        {
        }

        void Apply()
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;
            int dark = isDark ? 1 : 0;
            _ = Win32.DwmSetWindowAttribute(hwnd, Win32.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
            int round = 2; // DWMWCP_ROUND
            _ = Win32.DwmSetWindowAttribute(hwnd, Win32.DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
        }

        if (new WindowInteropHelper(window).Handle != IntPtr.Zero)
            Apply();
        else
            window.SourceInitialized += (_, _) => Apply();
    }

    public static void ApplyMicaBackdrop(Window window, bool isDark, int backdropType = Win32.DWMSBT_TABBEDWINDOW)
    {
        try
        {
            window.Icon = System.Windows.Media.Imaging.BitmapFrame.Create(
                new Uri("pack://application:,,,/Assets/snugwin.ico"));
        }
        catch
        {
        }

        void Apply()
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;

            // Remove WS_SYSMENU / WS_MINIMIZEBOX / WS_MAXIMIZEBOX so DWM never paints native
            // min/max/close glyphs over our custom titlebar chrome buttons, while preserving
            // WS_CAPTION | WS_THICKFRAME for Win11 Mica, rounded corners & snap resize.
            long style = GetWindowLongPtrW(hwnd, GWL_STYLE);
            long cleaned = style & ~(WS_SYSMENU | WS_MINIMIZEBOX | WS_MAXIMIZEBOX);
            if (cleaned != style)
            {
                SetWindowLongPtrW(hwnd, GWL_STYLE, cleaned);
                SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                    SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
            }

            int dark = isDark ? 1 : 0;
            _ = Win32.DwmSetWindowAttribute(hwnd, Win32.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
            int round = 2; // DWMWCP_ROUND
            _ = Win32.DwmSetWindowAttribute(hwnd, Win32.DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));

            bool isWin11MicaSupported = Environment.OSVersion.Version.Build >= 22621;
            if (isWin11MicaSupported)
            {
                if (HwndSource.FromHwnd(hwnd)?.CompositionTarget is { } target)
                    target.BackgroundColor = System.Windows.Media.Colors.Transparent;

                var margins = new Win32.MARGINS
                {
                    LeftWidth = -1,
                    RightWidth = -1,
                    TopHeight = -1,
                    BottomHeight = -1
                };
                int hrExtend = Win32.DwmExtendFrameIntoClientArea(hwnd, ref margins);
                int hrBackdrop = Win32.DwmSetWindowAttribute(hwnd, Win32.DWMWA_SYSTEMBACKDROP_TYPE, ref backdropType, sizeof(int));
                if (hrExtend == 0 && hrBackdrop == 0)
                {
                    window.Background = System.Windows.Media.Brushes.Transparent;
                    return;
                }
            }

            window.Background = new System.Windows.Media.SolidColorBrush(
                isDark
                    ? System.Windows.Media.Color.FromRgb(0x14, 0x17, 0x1F)
                    : System.Windows.Media.Color.FromRgb(0xF2, 0xF4, 0xF8));
        }

        if (new WindowInteropHelper(window).Handle != IntPtr.Zero)
            Apply();
        else
            window.SourceInitialized += (_, _) => Apply();
    }
}
