using System;
using System.Collections.Generic;
using System.Linq;

namespace SnugWin.Core.Session;

public abstract record SessionTransition
{
    public sealed record Began(IReadOnlyList<string> Controllers) : SessionTransition;
    public sealed record Resumed(IReadOnlyList<string> Controllers) : SessionTransition;
    public sealed record ControllersChanged(IReadOnlyList<string> Controllers) : SessionTransition;
    public sealed record Interrupted : SessionTransition;
    public sealed record Ended : SessionTransition;
}

public sealed class RemoteSession
{
    public ConnectionTracker Tracker { get; } = new();
    public SessionEpisode Episode { get; } = new();

    private List<string> _group = new();
    private readonly Dictionary<string, DateTimeOffset> _departed = new();
    private bool _waitingForStableDisconnectAfterManualEnd;
    private DateTimeOffset? _manualEndAbsentSince;

    public DateTimeOffset? EpisodeBeganAt { get; private set; }
    public bool IsConnected => Tracker.IsConnected;
    public IReadOnlyList<string> Controllers => Tracker.Controllers;
    public bool InEpisode => Episode.IsActive;
    public bool IsInReconnectGrace => Episode.IsInterrupted;

    public IReadOnlyList<string> EpisodeControllers(DateTimeOffset now)
    {
        var lingering = _departed
            .Where(kv => now - kv.Value <= SessionEpisode.ReconnectWindow)
            .Select(kv => kv.Key)
            .OrderBy(k => k, StringComparer.Ordinal);
        return _group.Concat(lingering).ToList();
    }

    public IReadOnlyList<SessionTransition> Update(UUSignals signals, DateTimeOffset now)
    {
        if (_waitingForStableDisconnectAfterManualEnd)
        {
            if (signals.IsPresent)
            {
                _manualEndAbsentSince = null;
                return Array.Empty<SessionTransition>();
            }

            _manualEndAbsentSince ??= now;
            if (now - _manualEndAbsentSince.Value < ConnectionTracker.DisconnectDebounce)
                return Array.Empty<SessionTransition>();

            _waitingForStableDisconnectAfterManualEnd = false;
            _manualEndAbsentSince = null;
            Tracker.Reset();
        }

        var transitions = new List<SessionTransition>();
        if (Episode.Advance(now) == EpisodeChange.Ended)
        {
            transitions.Add(new SessionTransition.Ended());
            _group.Clear();
            _departed.Clear();
            EpisodeBeganAt = null;
        }

        switch (Tracker.Update(signals, now))
        {
            case ConnectionEvent.Connected c:
                if (Episode.Connected(now) == EpisodeChange.Began)
                {
                    EpisodeBeganAt = now;
                    _group = c.Controllers.ToList();
                    _departed.Clear();
                    transitions.Add(new SessionTransition.Began(_group));
                }
                else
                {
                    if (c.Controllers.Count > 0) Regroup(c.Controllers, now);
                    transitions.Add(new SessionTransition.Resumed(c.Controllers));
                }
                break;

            case ConnectionEvent.ControllersChanged ch:
                Regroup(ch.Controllers, now);
                transitions.Add(new SessionTransition.ControllersChanged(ch.Controllers));
                break;

            case ConnectionEvent.Disconnected:
                if (Episode.Disconnected(now) == EpisodeChange.Interrupted)
                    transitions.Add(new SessionTransition.Interrupted());
                break;
        }

        return transitions;
    }

    public SessionTransition? EndNow()
    {
        if (Episode.EndNow() != EpisodeChange.Ended)
            return null;
        _group.Clear();
        _departed.Clear();
        EpisodeBeganAt = null;
        _waitingForStableDisconnectAfterManualEnd = true;
        _manualEndAbsentSince = null;
        return new SessionTransition.Ended();
    }

    public void NoteSelfInducedChange(DateTimeOffset now) => Tracker.NoteSelfInducedChange(now);

    private void Regroup(IReadOnlyList<string> names, DateTimeOffset now)
    {
        var nextSet = new HashSet<string>(names);
        foreach (var old in _group)
        {
            if (!nextSet.Contains(old))
                _departed[old] = now;
        }
        foreach (var n in names)
            _departed.Remove(n);
        _group = names.ToList();
    }
}
