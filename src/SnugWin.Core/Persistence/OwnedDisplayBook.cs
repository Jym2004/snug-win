using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace SnugWin.Core.Persistence;

/// <summary>Tracks virtual display IDs created by SnugWin so startup cleanup never touches unrelated displays.</summary>
public sealed record OwnedDisplayBook
{
    public IReadOnlyList<uint> DisplayIds { get; init; } = Array.Empty<uint>();

    public OwnedDisplayBook WithAdded(uint displayId)
    {
        if (DisplayIds.Contains(displayId)) return this;
        var next = new List<uint>(DisplayIds.Count + 1);
        next.AddRange(DisplayIds);
        next.Add(displayId);
        return this with { DisplayIds = next };
    }

    public OwnedDisplayBook WithRemoved(uint displayId)
    {
        if (!DisplayIds.Contains(displayId)) return this;
        return this with { DisplayIds = DisplayIds.Where(id => id != displayId).ToArray() };
    }
}

public sealed class OwnedDisplayStore
{
    private readonly JsonFileStore<OwnedDisplayBook> _store;

    public OwnedDisplayStore(string filePath)
    {
        _store = new JsonFileStore<OwnedDisplayBook>(filePath);
    }

    public IReadOnlyList<uint> LoadIds() =>
        (_store.LoadOrQuarantine() ?? new OwnedDisplayBook()).DisplayIds;

    public void RecordOwned(uint displayId)
    {
        var book = (_store.LoadOrQuarantine() ?? new OwnedDisplayBook()).WithAdded(displayId);
        _store.Save(book);
    }

    public void ReleaseOwned(uint displayId)
    {
        var book = (_store.LoadOrQuarantine() ?? new OwnedDisplayBook()).WithRemoved(displayId);
        if (book.DisplayIds.Count == 0)
            _store.Delete();
        else
            _store.Save(book);
    }

    public void ClearAll() => _store.Delete();
}
