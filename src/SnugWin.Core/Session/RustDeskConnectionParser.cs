using System;
using System.Collections.Generic;
using System.Linq;

namespace SnugWin.Core.Session;

/// <summary>An in-memory accessibility snapshot. Callers must not persist or log peer-provided names.</summary>
public sealed record RustDeskAccessibleNode(
    string? Name,
    IReadOnlyList<RustDeskAccessibleNode> Children,
    int Role = 0,
    bool IsComplete = true)
{
    public static RustDeskAccessibleNode Text(string name) => new(name, Array.Empty<RustDeskAccessibleNode>());
    public static RustDeskAccessibleNode Group(params RustDeskAccessibleNode[] children) => new(null, children);
}

/// <summary>Recognizes an authorized desktop session only from its own CM card subtree.</summary>
public static class RustDeskConnectionParser
{
    private sealed record Entry(RustDeskAccessibleNode Node, Entry? Parent);
    private enum CardState { Connected, Disconnected, Pending }

    /// <summary>
    /// Returns true for a confirmed desktop session, false for a complete snapshot showing only
    /// known inactive/non-desktop cards, and null when the tree does not establish either result.
    /// </summary>
    public static bool? DetectAuthorizedDesktopConnection(IEnumerable<RustDeskAccessibleNode> roots)
    {
        ArgumentNullException.ThrowIfNull(roots);
        var rootList = roots.ToList();
        if (rootList.Count == 0) return null;
        var completeRoots = rootList.Where(IsComplete).ToList();
        if (completeRoots.Count == 0) return null;

        var entries = new List<Entry>();
        foreach (var root in completeRoots)
            AddTree(root, null, entries, depth: 0);

        bool sawKnownCard = false;
        foreach (var entry in entries)
        {
            if (!TryGetCardState(entry.Node.Name, out var state)) continue;
            if (!TryFindCard(entry, out var card, out var activeBranch)) continue;

            sawKnownCard = true;
            if (state != CardState.Connected) continue;
            if (ContainsNonDesktopType(card.Node)) continue;
            if (ContainsDisconnectAction(activeBranch.Node)) return true;
        }

        return sawKnownCard ? false : null;
    }

    public static bool HasAuthorizedDesktopConnection(IEnumerable<RustDeskAccessibleNode> roots) =>
        DetectAuthorizedDesktopConnection(roots) == true;

    private static void AddTree(RustDeskAccessibleNode node, Entry? parent, List<Entry> entries, int depth)
    {
        if (depth > 32 || entries.Count >= 4096) return;
        var entry = new Entry(node, parent);
        entries.Add(entry);
        foreach (var child in node.Children)
            AddTree(child, entry, entries, depth + 1);
    }

    private static bool TryFindCard(Entry statusEntry, out Entry card, out Entry activeBranch)
    {
        // The observed Flutter tree has a status grouping node (role 20), nested in the
        // active branch of a two-branch card. The sibling branch is the header/type branch.
        activeBranch = null!;
        if (statusEntry.Node.Children.Count < 1
            || (statusEntry.Node.Role != 0 && statusEntry.Node.Role != 20))
        {
            card = null!;
            return false;
        }

        for (Entry? parent = statusEntry.Parent; parent is not null; parent = parent.Parent)
        {
            Entry branch = statusEntry;
            while (branch.Parent is not null && branch.Parent != parent)
                branch = branch.Parent;
            if (branch.Parent != parent || parent.Node.Children.Count != 2) continue;

            bool hasHeaderBranch = parent.Node.Children.Any(child =>
                !ReferenceEquals(child, branch.Node) && !ContainsCardState(child));
            if (!hasHeaderBranch) continue;

            card = parent;
            activeBranch = branch;
            return true;
        }

        card = null!;
        return false;
    }

    private static bool ContainsCardState(RustDeskAccessibleNode root) =>
        DescendantsAndSelf(root).Any(node => GetLines(node.Name).Any(line =>
            IsConnectedLabel(line) || IsDisconnectedLabel(line) || IsPendingLabel(line)));

    private static bool ContainsDisconnectAction(RustDeskAccessibleNode root) =>
        DescendantsAndSelf(root).Any(node =>
            HasExactLine(node.Name, IsDisconnectLabel) && (node.Role == 0 || node.Role == 41));

    private static bool ContainsNonDesktopType(RustDeskAccessibleNode root) =>
        DescendantsAndSelf(root).Any(node => GetLines(node.Name).Any(IsNonDesktopTypeLabel));

    private static IEnumerable<RustDeskAccessibleNode> DescendantsAndSelf(RustDeskAccessibleNode root)
    {
        var stack = new Stack<RustDeskAccessibleNode>();
        stack.Push(root);
        int visited = 0;
        while (stack.Count > 0 && visited++ < 4096)
        {
            var current = stack.Pop();
            yield return current;
            for (int i = current.Children.Count - 1; i >= 0; i--)
                stack.Push(current.Children[i]);
        }
    }

    private static bool IsComplete(RustDeskAccessibleNode root)
    {
        var stack = new Stack<RustDeskAccessibleNode>();
        stack.Push(root);
        int visited = 0;
        while (stack.Count > 0 && visited++ < 4096)
        {
            var current = stack.Pop();
            if (!current.IsComplete || current.Children.Count > 512) return false;
            foreach (var child in current.Children) stack.Push(child);
        }
        return stack.Count == 0;
    }

    private static bool TryGetCardState(string? name, out CardState state)
    {
        var lines = GetLines(name);
        bool connected = lines.Any(IsConnectedLabel);
        bool disconnected = lines.Any(IsDisconnectedLabel);
        bool pending = lines.Any(IsPendingLabel);
        if ((connected ? 1 : 0) + (disconnected ? 1 : 0) + (pending ? 1 : 0) != 1)
        {
            state = default;
            return false;
        }

        state = connected ? CardState.Connected : disconnected ? CardState.Disconnected : CardState.Pending;
        return true;
    }

    private static string[] GetLines(string? value) => string.IsNullOrWhiteSpace(value)
        ? Array.Empty<string>()
        : value.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool HasExactLine(string? value, Func<string, bool> matcher) => GetLines(value).Any(matcher);
    private static bool IsConnectedLabel(string value) => IsOneOf(value, "connected", "已连接");
    private static bool IsDisconnectedLabel(string value) => IsOneOf(value, "disconnected", "已断开");
    private static bool IsPendingLabel(string value) => IsOneOf(value,
        "request access", "request access to your device", "请求访问", "请求访问您的设备", "connecting", "正在连接");
    private static bool IsDisconnectLabel(string value) => IsOneOf(value, "disconnect", "断开连接");

    private static bool IsNonDesktopTypeLabel(string value) => IsOneOf(value,
        "transfer file", "file transfer", "文件传输", "传输文件",
        "view camera", "camera", "摄像头", "terminal", "终端",
        "port forward", "port forwarding", "端口转发")
        || value.StartsWith("port forward:", StringComparison.OrdinalIgnoreCase)
        || value.StartsWith("端口转发：", StringComparison.Ordinal);

    private static bool IsOneOf(string value, params string[] candidates) =>
        candidates.Any(candidate => value.Equals(candidate, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Debounces distinct UI snapshots and expires stale active state.</summary>
public sealed class RustDeskPresenceTracker
{
    public static readonly TimeSpan StaleSampleTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MinimumTransitionInterval = TimeSpan.FromSeconds(1);
    private const int SamplesToTransition = 2;
    private int _positiveSamples;
    private int _negativeSamples;
    private DateTimeOffset? _lastDefiniteSampleAt;
    private DateTimeOffset? _lastCountedSampleAt;

    public bool IsActive { get; private set; }

    public bool Observe(bool? sample, DateTimeOffset now)
    {
        Expire(now);
        if (sample is null)
        {
            _positiveSamples = 0;
            _negativeSamples = 0;
            return IsActive;
        }
        if (_lastCountedSampleAt is { } previous && now - previous < MinimumTransitionInterval)
            return IsActive;

        _lastCountedSampleAt = now;
        _lastDefiniteSampleAt = now;
        if (sample.Value)
        {
            _positiveSamples++;
            _negativeSamples = 0;
            if (_positiveSamples >= SamplesToTransition) IsActive = true;
        }
        else
        {
            _negativeSamples++;
            _positiveSamples = 0;
            if (_negativeSamples >= SamplesToTransition) IsActive = false;
        }
        return IsActive;
    }

    public bool Expire(DateTimeOffset now)
    {
        if (_lastDefiniteSampleAt is { } last && now - last >= StaleSampleTimeout)
        {
            IsActive = false;
            _positiveSamples = 0;
            _negativeSamples = 0;
            _lastCountedSampleAt = null;
        }
        return IsActive;
    }
}
