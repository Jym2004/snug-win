using System;
using System.Collections.Generic;
using System.Linq;
using SnugWin.Core.Persistence;

namespace SnugWin.Core.PhoneScreen;

public sealed class LeftBehindEntry
{
    public PhoneRecord Record { get; set; } = new();
    public DateTimeOffset Since { get; set; }

    public ZoomScope? Scope => ZoomBook.ScopeOf(Record.AppId);

    public PhoneRecord ReturnedAs(WindowKey key)
    {
        var r = Record with { Window = key };
        if (!CarriesZoom(key.Pid))
            r.Zoom = 0;
        return r;
    }

    public bool CarriesZoom(int pid) => Scope switch
    {
        ZoomScope.RestoredWindow or ZoomScope.Workspace => true,
        ZoomScope.LiveWindow => pid == Record.Window.Pid,
        _ => false
    };
}

public sealed class LeftBehindBook
{
    public static readonly TimeSpan KeepDuration = TimeSpan.FromDays(14);
    public const int Limit = 20;

    public List<LeftBehindEntry> Entries { get; set; } = new();

    public void Add(LeftBehindEntry entry)
    {
        if (entry.Record.Filled is null) return;
        Entries.Add(entry);
    }

    public void RemoveAt(int index) => Entries.RemoveAt(index);

    public void Prune(DateTimeOffset now)
    {
        Entries.RemoveAll(e => now - e.Since > KeepDuration);
        Entries.Sort((a, b) => a.Since.CompareTo(b.Since));
        if (Entries.Count > Limit)
            Entries.RemoveRange(0, Entries.Count - Limit);
    }

    public bool Awaits(string appId, SizeD size) =>
        Entries.Any(e => string.Equals(e.Record.AppId, appId, StringComparison.OrdinalIgnoreCase) && IsFilled(e, size));

    public int? Match(WindowInfo window, RectD frame)
    {
        var candidates = new List<int>();
        for (int i = 0; i < Entries.Count; i++)
        {
            if (string.Equals(Entries[i].Record.AppId, window.AppId, StringComparison.OrdinalIgnoreCase)
                && IsFilled(Entries[i], frame.Size))
            {
                candidates.Add(i);
            }
        }
        if (candidates.Count == 0) return null;

        int? Newest(IEnumerable<int> indices)
        {
            int? best = null;
            foreach (int idx in indices)
            {
                if (best is null || Entries[idx].Since > Entries[best.Value].Since)
                    best = idx;
            }
            return best;
        }

        return Newest(candidates.Where(i => Entries[i].Record.Window == window.Key))
            ?? Newest(candidates.Where(i => Entries[i].Record.Title == window.Title))
            ?? Newest(candidates);
    }

    private static bool IsFilled(LeftBehindEntry entry, SizeD size)
    {
        if (entry.Record.Filled is not { } filled) return false;
        return Math.Abs(filled.Width - size.Width) < 2.0 && Math.Abs(filled.Height - size.Height) < 2.0;
    }
}
