using System;

namespace SnugWin.Core.Session;

/// <summary>
/// Caches a target process PID across sensor ticks to avoid enumerating all OS processes
/// on every 500ms tick when the process is absent or still alive.
/// Mirrors upstream Snug v2.7.2 UUProcessWatch (PR #39).
/// </summary>
public sealed class UUProcessWatch
{
    public static readonly TimeSpan DefaultRescanCooldown = TimeSpan.FromSeconds(3);

    private readonly Func<int?> _scan;
    private readonly Func<int, bool> _isAlive;
    private readonly TimeSpan _rescanCooldown;

    private int? _cachedPid;
    private DateTimeOffset? _lastScanAt;

    public UUProcessWatch(
        Func<int?> scan,
        Func<int, bool> isAlive,
        TimeSpan? rescanCooldown = null)
    {
        _scan = scan;
        _isAlive = isAlive;
        _rescanCooldown = rescanCooldown ?? DefaultRescanCooldown;
    }

    public int? CachedPid => _cachedPid;

    public int? Resolve(DateTimeOffset now)
    {
        if (_cachedPid is { } pid)
        {
            if (_isAlive(pid))
                return pid;
            _cachedPid = null;
        }

        if (_lastScanAt is { } last && now - last < _rescanCooldown)
            return null;

        _lastScanAt = now;
        _cachedPid = _scan();
        return _cachedPid;
    }

    public void Invalidate()
    {
        _cachedPid = null;
        _lastScanAt = null;
    }
}
