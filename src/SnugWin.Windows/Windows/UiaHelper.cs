using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows.Automation;
using SnugWin.Core.PhoneScreen;
using SnugWin.Core.Session;
using SnugWin.Core.Support;

namespace SnugWin.Windows.Windows;

internal static class UiaHelper
{
    public static RectD? FindCloseButtonRect(IntPtr hwnd)
    {
        try
        {
            var root = AutomationElement.FromHandle(hwnd);
            if (root is null) return null;

            var cond = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button);
            var buttons = root.FindAll(TreeScope.Descendants, cond);
            foreach (AutomationElement btn in buttons)
            {
                string autoId = btn.Current.AutomationId ?? string.Empty;
                string name = btn.Current.Name ?? string.Empty;
                if (autoId.Equals("Close", StringComparison.OrdinalIgnoreCase)
                    || autoId.Equals("PART_Close", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("关闭", StringComparison.Ordinal)
                    || name.Equals("Close", StringComparison.OrdinalIgnoreCase))
                {
                    var r = btn.Current.BoundingRectangle;
                    if (!r.IsEmpty && r.Width > 0 && r.Height > 0)
                        return new RectD(r.X, r.Y, r.Width, r.Height);
                }
            }
        }
        catch
        {
            // UIA may throw if window closes or app hangs
        }
        return null;
    }

    public static bool TryInvokeNewWindowMenu(IntPtr hwnd)
    {
        try
        {
            var root = AutomationElement.FromHandle(hwnd);
            if (root is null) return false;

            var menuCond = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.MenuItem);
            var items = root.FindAll(TreeScope.Descendants, menuCond);
            foreach (AutomationElement item in items)
            {
                string name = item.Current.Name ?? string.Empty;
                if (name.Contains("新建窗口", StringComparison.Ordinal)
                    || name.Contains("New Window", StringComparison.OrdinalIgnoreCase))
                {
                    if (item.TryGetCurrentPattern(InvokePattern.Pattern, out object? pattern) && pattern is InvokePattern invoke)
                    {
                        invoke.Invoke();
                        return true;
                    }
                }
            }
        }
        catch
        {
            // ignore UIA failures
        }
        return false;
    }

    private const int MaxUiaDepth = 25;
    private const int MaxUiaNodes = 2048;
    private const string HomePageMainWindowClass = "client_ui::home::HomePageMainWindow";
    private static string? _lastUiaError;

    public static ControllerTable ReadGameViewerControllers(IEnumerable<IntPtr> hwnds)
    {
        var trees = new List<UuElement>();

        foreach (var hwnd in hwnds)
        {
            try
            {
                var root = AutomationElement.FromHandle(hwnd);
                if (root is null) continue;
                // UU keeps its Qt home window hidden while being controlled; that window
                // still carries the controller panel, so it must not be filtered out here.
                if (!Native.Win32.IsWindowVisible(hwnd)
                    && root.Current.ClassName != HomePageMainWindowClass) continue;

                int nodeCount = 0;
                bool aborted = false;
                var visited = new HashSet<string>(StringComparer.Ordinal);
                var snapshot = SnapshotElement(root, depth: 0, ref nodeCount, ref aborted, visited);
                if (!aborted && snapshot is not null)
                {
                    _lastUiaError = null;
                    trees.Add(snapshot);
                }
            }
            catch (Exception ex)
            {
                string key = $"{hwnd.ToInt64():X}:{ex.GetType().Name}";
                if (!string.Equals(_lastUiaError, key, StringComparison.Ordinal))
                {
                    _lastUiaError = key;
                    Log.Warn($"读取 UU 控制端 UIA 失败（hwnd=0x{hwnd.ToInt64():X}）：{ex.GetType().Name}");
                }
            }
        }

        if (trees.Count == 0) return ControllerTable.Unreadable;
        return ControllerTableParser.ParseWindows(trees);
    }

    private static string? TryGetClassName(AutomationElement element)
    {
        try { return element.Current.ClassName; }
        catch { return null; }
    }

    /// <summary>
    /// Qt only materializes the "本机被控中" subtree after the home window has been shown at
    /// least once. When UU runs minimized/never displayed the tree is empty, which would make
    /// SnugWin treat an unknown controller as a phone. Park the window off-screen, show it
    /// without activating, read the panel, then restore its previous placement and state.
    /// </summary>
    public static bool TryWakeAndRead(IntPtr hwnd, out ControllerTable table)
    {
        table = ControllerTable.Unreadable;

        AutomationElement? root;
        try { root = AutomationElement.FromHandle(hwnd); }
        catch { return false; }
        if (root is null || TryGetClassName(root) != HomePageMainWindowClass) return false;

        var existing = SnapshotToTable(root);
        if (existing is { } ready && ready.Names.Count > 0)
        {
            table = ready;
            return true;
        }

        if (Native.Win32.GetForegroundWindow() == hwnd) return false;
        if (!Native.Win32.GetWindowRect(hwnd, out var before)) return false;

        bool wasMinimized = Native.Win32.IsIconic(hwnd);
        bool wasVisible = Native.Win32.IsWindowVisible(hwnd);
        bool moved = false;
        try
        {
            moved = Native.Win32.SetWindowPos(hwnd, IntPtr.Zero, -32000, -32000, before.Width, before.Height,
                Native.Win32.SWP_NOZORDER | Native.Win32.SWP_NOACTIVATE | Native.Win32.SWP_NOSIZE);
            if (!moved) return false;

            Native.Win32.ShowWindow(hwnd, Native.Win32.SW_SHOWNOACTIVATE);
            Thread.Sleep(200);

            AutomationElement? woken;
            try { woken = AutomationElement.FromHandle(hwnd); }
            catch { woken = null; }
            if (woken is null || TryGetClassName(woken) != HomePageMainWindowClass) return false;

            var parsed = SnapshotToTable(woken);
            if (parsed is null) return false;
            table = parsed;
            return true;
        }
        finally
        {
            if (moved)
            {
                // Restore hidden/minimized states independently before/with placement so a
                // minimized+hidden window is never left visible on the taskbar.
                if (!wasVisible) Native.Win32.ShowWindow(hwnd, Native.Win32.SW_HIDE);
                Native.Win32.SetWindowPos(hwnd, IntPtr.Zero, before.Left, before.Top, before.Width, before.Height,
                    Native.Win32.SWP_NOZORDER | Native.Win32.SWP_NOACTIVATE);
                if (wasMinimized) Native.Win32.ShowWindow(hwnd, Native.Win32.SW_MINIMIZE);
                if (!wasVisible) Native.Win32.ShowWindow(hwnd, Native.Win32.SW_HIDE);
            }
        }
    }

    private static ControllerTable? SnapshotToTable(AutomationElement root)
    {
        try
        {
            int nodeCount = 0;
            bool aborted = false;
            var visited = new HashSet<string>(StringComparer.Ordinal);
            var snap = SnapshotElement(root, depth: 0, ref nodeCount, ref aborted, visited);
            if (aborted || snap is null) return null;
            return ControllerTableParser.ParseWindows([snap]);
        }
        catch
        {
            return null;
        }
    }

    private static UuElement? SnapshotElement(
        AutomationElement element,
        int depth,
        ref int nodeCount,
        ref bool aborted,
        HashSet<string> path)
    {
        if (depth >= MaxUiaDepth || ++nodeCount > MaxUiaNodes)
        {
            aborted = true;
            return null;
        }

        string runtimeKey = string.Join(",", element.GetRuntimeId() ?? Array.Empty<int>());
        if (!string.IsNullOrEmpty(runtimeKey) && !path.Add(runtimeKey))
            return null;

        try
        {
            var children = new List<UuElement>();
            var walker = TreeWalker.RawViewWalker;
            var child = walker.GetFirstChild(element);
            while (child is not null)
            {
                var snap = SnapshotElement(child, depth + 1, ref nodeCount, ref aborted, path);
                if (aborted) return null;
                if (snap is not null) children.Add(snap);
                child = walker.GetNextSibling(child);
            }

            var current = element.Current;
            var ct = current.ControlType;
            if (current.ClassName == "client_ui::controlled_window::ControllerInfoView")
                return new UuElement(UuElementKind.ControlledPanel, null, children);
            if (current.ClassName == "client_ui::controlled_window::ControllerItemWidget")
                return UuElement.RowNode(children);
            if (ct == ControlType.Text)
            {
                string name = element.Current.Name ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(name))
                    return new UuElement(UuElementKind.Text, name.Trim(), children);
            }
            if (ct == ControlType.Table || ct == ControlType.DataGrid || ct == ControlType.List)
                return UuElement.TableNode(children);
            if (ct == ControlType.DataItem || ct == ControlType.ListItem)
                return UuElement.RowNode(children);

            return UuElement.OtherNode(children);
        }
        finally
        {
            if (!string.IsNullOrEmpty(runtimeKey))
                path.Remove(runtimeKey);
        }
    }
}
