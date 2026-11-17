using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Automation;
using SnugWin.Core.PhoneScreen;
using SnugWin.Core.Session;

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

    public static ControllerTable ReadGameViewerControllers(IEnumerable<IntPtr> hwnds)
    {
        var trees = new List<UuElement>();
        int nodeCount = 0;
        bool aborted = false;

        foreach (var hwnd in hwnds)
        {
            try
            {
                var root = AutomationElement.FromHandle(hwnd);
                if (root is null) continue;
                var visited = new HashSet<string>(StringComparer.Ordinal);
                var snapshot = SnapshotElement(root, depth: 0, ref nodeCount, ref aborted, visited);
                if (aborted) return ControllerTable.Unreadable;
                if (snapshot is not null) trees.Add(snapshot);
            }
            catch
            {
                // ignore individual UIA window error
            }
        }

        if (trees.Count == 0) return ControllerTable.Unreadable;
        return ControllerTableParser.ParseWindows(trees);
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

            var ct = element.Current.ControlType;
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
