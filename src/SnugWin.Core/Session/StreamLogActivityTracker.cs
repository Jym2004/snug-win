using System;

namespace SnugWin.Core.Session;

/// <summary>
/// Tracks GameViewerServer streamer log byte growth to distinguish active remote streaming
/// from idle/post-disconnect control sockets or single isolated heartbeat flushes.
/// </summary>
public sealed class StreamLogActivityTracker
{
    public static readonly TimeSpan AdvancePairMaxGap = TimeSpan.FromSeconds(3.0);
    public static readonly TimeSpan ActiveHoldWindow = TimeSpan.FromSeconds(2.0);

    private int _serverPid;
    private long _lastBytes = -1;
    private DateTimeOffset _prevAdvance = DateTimeOffset.MinValue;
    private DateTimeOffset _lastAdvance = DateTimeOffset.MinValue;

    public void Reset()
    {
        _serverPid = 0;
        _lastBytes = -1;
        _prevAdvance = DateTimeOffset.MinValue;
        _lastAdvance = DateTimeOffset.MinValue;
    }

    public bool? Observe(int? serverPid, long? totalBytes, bool logFilesExist, DateTimeOffset now)
    {
        if (serverPid is not { } pid)
        {
            Reset();
            return false;
        }

        if (pid != _serverPid)
        {
            Reset();
            _serverPid = pid;
        }

        if (!logFilesExist || totalBytes is not { } bytes)
            return null;

        if (_lastBytes < 0)
        {
            _lastBytes = bytes;
            return false;
        }

        if (bytes > _lastBytes)
        {
            _lastBytes = bytes;
            _prevAdvance = _lastAdvance;
            _lastAdvance = now;
        }

        bool continuous = _prevAdvance != DateTimeOffset.MinValue
                          && _lastAdvance - _prevAdvance <= AdvancePairMaxGap;
        return continuous && now - _lastAdvance < ActiveHoldWindow;
    }
}
