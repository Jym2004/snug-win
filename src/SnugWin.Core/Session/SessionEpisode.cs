using System;

namespace SnugWin.Core.Session;

public enum EpisodeChange
{
    Began,
    Resumed,
    Interrupted,
    Ended
}

public sealed class SessionEpisode
{
    public static readonly TimeSpan ReconnectWindow = TimeSpan.FromSeconds(15);

    private enum Phase { Idle, Live, Interrupted }

    private Phase _phase = Phase.Idle;
    private DateTimeOffset _interruptedSince;

    public bool IsActive => _phase != Phase.Idle;

    public EpisodeChange Connected(DateTimeOffset now)
    {
        if (Advance(now) == EpisodeChange.Ended)
            return Begin();

        switch (_phase)
        {
            case Phase.Idle:
                return Begin();
            case Phase.Live:
            case Phase.Interrupted:
                _phase = Phase.Live;
                return EpisodeChange.Resumed;
            default:
                return Begin();
        }
    }

    public EpisodeChange? Disconnected(DateTimeOffset now)
    {
        if (_phase != Phase.Live) return null;
        _phase = Phase.Interrupted;
        _interruptedSince = now;
        return EpisodeChange.Interrupted;
    }

    public EpisodeChange? Advance(DateTimeOffset now)
    {
        if (_phase != Phase.Interrupted || now - _interruptedSince <= ReconnectWindow)
            return null;
        _phase = Phase.Idle;
        return EpisodeChange.Ended;
    }

    private EpisodeChange Begin()
    {
        _phase = Phase.Live;
        return EpisodeChange.Began;
    }
}
