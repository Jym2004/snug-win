using System;
using System.Diagnostics;
using System.Threading.Tasks;
using SnugWin.Core.Session;

namespace SnugWin.Windows.Sensing;

// Polled by the serialized sensor loop. A stalled UIA provider keeps its one slot, even on PID changes.
// Uses a monotonic clock and stamps TTL before the UIA read begins so stalled reads expire immediately.
internal sealed class ControllerTableProbe
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(3);
    private static readonly long ClockOrigin = Stopwatch.GetTimestamp();

    private readonly Func<int, bool, ControllerTable> _read;
    private readonly Func<TimeSpan> _monotonicNow;
    private Task<(int Pid, ControllerTable Table, TimeSpan ExpiresAt)>? _pending;
    private (ControllerTable Table, TimeSpan ExpiresAt)? _latest;
    private int? _readPid;
    private TimeSpan _nextReadAt = TimeSpan.MinValue;

    public ControllerTableProbe(Func<int, bool, ControllerTable> read, Func<TimeSpan>? monotonicNow = null)
    {
        _read = read;
        _monotonicNow = monotonicNow ?? (() => Stopwatch.GetElapsedTime(ClockOrigin));
    }

    public ControllerTableProbe(Func<int, ControllerTable> read, Func<DateTimeOffset>? testNow = null)
        : this(
            (pid, _) => read(pid),
            testNow is null ? null : () => testNow() - DateTimeOffset.UnixEpoch)
    {
    }

    public ControllerTable Poll(int? clientPid, bool sessionActive = false) =>
        PollAt(clientPid, _monotonicNow(), sessionActive);

    public ControllerTable Poll(int? clientPid, DateTimeOffset now, bool sessionActive = false) =>
        PollAt(clientPid, now - DateTimeOffset.UnixEpoch, sessionActive);

    internal ControllerTable PollAt(int? clientPid, TimeSpan now, bool sessionActive = false)
    {
        if (_readPid != clientPid)
        {
            _readPid = null;
            _latest = default;
            _nextReadAt = TimeSpan.MinValue;
        }

        if (_pending is { IsCompleted: true } completed)
        {
            var result = completed.Result;
            if (_readPid == result.Pid)
                _latest = (result.Table, result.ExpiresAt);
            _pending = null;
        }

        if (_pending is null && clientPid is { } pid && now >= _nextReadAt)
        {
            _readPid = pid;
            _nextReadAt = now + RefreshInterval;
            // Stamp age before the UIA call starts so a read that stalls past MaxAge expires on completion.
            var startedAt = _monotonicNow();
            _pending = Task.Run(() => (pid, ReadOrUnreadable(pid, sessionActive), startedAt + MaxAge));
        }
        return _latest is { } last && now < last.ExpiresAt ? last.Table : ControllerTable.Unreadable;
    }

    private ControllerTable ReadOrUnreadable(int pid, bool sessionActive)
    {
        try { return _read(pid, sessionActive); }
        catch (Exception) { return ControllerTable.Unreadable; }
    }
}
