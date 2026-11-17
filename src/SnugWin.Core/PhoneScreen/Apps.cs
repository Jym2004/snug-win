using System;
using System.Collections.Generic;
using System.Linq;

namespace SnugWin.Core.PhoneScreen;

public sealed record LetterGroup(string Letter, IReadOnlyList<AppInfo> Apps);

public sealed record AppSearchResult(IReadOnlyList<AppInfo> Apps, IReadOnlyList<WindowInfo> Windows)
{
    public bool IsEmpty => Apps.Count == 0 && Windows.Count == 0;
}

public abstract record AppAction
{
    public sealed record Pull(WindowInfo Window) : AppAction;
    public sealed record Open(AppInfo App) : AppAction;
}

/// <summary>
/// Port of SnugCore AppList: ordering running apps by Z-order, keeping order while list is visible,
/// grouping installed apps A-Z/#, searching by pinyin/initials, and choosing Pull vs Open.
/// </summary>
public static class AppList
{
    public static IReadOnlyList<RunningApp> Ordered(
        IReadOnlyList<RunningApp> apps,
        IReadOnlyList<ulong> frontToBackHandles)
    {
        var rank = new Dictionary<ulong, int>(frontToBackHandles.Count);
        for (int i = 0; i < frontToBackHandles.Count; i++)
            rank.TryAdd(frontToBackHandles[i], i);

        int Position(WindowInfo w) => rank.TryGetValue(w.Key.Handle, out int pos) ? pos : int.MaxValue;

        var withSortedWindows = apps.Select(app =>
        {
            var sortedWins = app.Windows
                .Select((w, idx) => (Window: w, Pos: Position(w), Index: idx))
                .OrderBy(x => x.Pos)
                .ThenBy(x => x.Index)
                .Select(x => x.Window)
                .ToList();
            return app with { Windows = sortedWins };
        });

        return withSortedWindows
            .Select((a, idx) => (App: a, Pos: a.Windows.Count > 0 ? Position(a.Windows[0]) : int.MaxValue, Index: idx))
            .OrderBy(x => x.Pos)
            .ThenBy(x => x.Index)
            .Select(x => x.App)
            .ToList();
    }

    public static IReadOnlyList<RunningApp> KeepingOrder(
        IReadOnlyList<RunningApp> shown,
        IReadOnlyList<RunningApp> fresh)
    {
        var latestByKey = new Dictionary<string, RunningApp>(StringComparer.OrdinalIgnoreCase);
        foreach (var app in fresh)
            latestByKey.TryAdd(IdentityKey(app), app);

        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var kept = new List<RunningApp>();
        foreach (var old in shown)
        {
            string key = IdentityKey(old);
            if (!latestByKey.TryGetValue(key, out var updated)) continue;
            known.Add(key);
            kept.Add(updated with { Windows = KeepingWindowOrder(old.Windows, updated.Windows) });
        }

        foreach (var item in fresh)
        {
            if (!known.Contains(IdentityKey(item)))
                kept.Add(item);
        }
        return kept;
    }

    private static IReadOnlyList<WindowInfo> KeepingWindowOrder(
        IReadOnlyList<WindowInfo> shown,
        IReadOnlyList<WindowInfo> fresh)
    {
        var latest = new Dictionary<WindowKey, WindowInfo>();
        foreach (var w in fresh)
            latest.TryAdd(w.Key, w);

        var known = new HashSet<WindowKey>();
        var result = new List<WindowInfo>();
        foreach (var old in shown)
        {
            if (latest.TryGetValue(old.Key, out var cur))
            {
                known.Add(old.Key);
                result.Add(cur);
            }
        }
        foreach (var w in fresh)
        {
            if (!known.Contains(w.Key))
                result.Add(w);
        }
        return result;
    }

    public static IReadOnlyList<AppInfo> Sorted(IEnumerable<AppInfo> apps)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return apps
            .Where(a => seen.Add(a.AppId))
            .OrderBy(a => PinyinSearch.SortKey(a.Name), StringComparer.OrdinalIgnoreCase)
            .ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public static IReadOnlyList<LetterGroup> GroupedByLetter(IReadOnlyList<AppInfo> sortedApps)
    {
        var groups = new Dictionary<string, List<AppInfo>>(StringComparer.Ordinal);
        foreach (var app in sortedApps)
        {
            string key = PinyinSearch.SortKey(app.Name);
            string letter = key.Length > 0 && char.IsAsciiLetter(key[0])
                ? char.ToUpperInvariant(key[0]).ToString()
                : "#";
            if (!groups.TryGetValue(letter, out var list))
            {
                list = new List<AppInfo>();
                groups[letter] = list;
            }
            list.Add(app);
        }

        return groups
            .OrderBy(kv => kv.Key == "#" ? 1 : 0)
            .ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => new LetterGroup(kv.Key, kv.Value))
            .ToList();
    }

    public static AppSearchResult Search(
        string query,
        IReadOnlyList<AppInfo> installed,
        IReadOnlyList<RunningApp> running)
    {
        if (string.IsNullOrWhiteSpace(query))
            return new AppSearchResult(Array.Empty<AppInfo>(), Array.Empty<WindowInfo>());

        var installedIds = installed.Select(a => a.AppId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidates = installed.Concat(
            running.Where(r => !installedIds.Contains(r.App.AppId)).Select(r => r.App));

        var apps = candidates
            .Select((a, idx) => (App: a, Score: Math.Max(PinyinSearch.Score(a.Name, query), PinyinSearch.Score(a.AppId, query)), Index: idx))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Index)
            .Select(x => x.App)
            .ToList();

        var windows = running
            .SelectMany(r => r.Windows)
            .Where(w => PinyinSearch.Matches(w.Title, query))
            .ToList();

        return new AppSearchResult(apps, windows);
    }

    public static AppAction ActionFor(AppInfo app, IReadOnlyList<RunningApp> running)
    {
        var match = running.FirstOrDefault(r => string.Equals(r.App.AppId, app.AppId, StringComparison.OrdinalIgnoreCase));
        if (match is { Windows.Count: > 0 })
            return new AppAction.Pull(match.Windows[0]);
        return new AppAction.Open(app);
    }

    private static string IdentityKey(RunningApp app) => app.App.AppId;
}
