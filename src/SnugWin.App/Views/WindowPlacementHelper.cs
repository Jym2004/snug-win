using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using SnugWin.Core.PhoneScreen;
using SnugWin.Windows.Native;

namespace SnugWin.App.Views;

internal static class WindowPlacementHelper
{
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private const int GWL_EXSTYLE = -20;
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
}
