using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using Accessibility;
using SnugWin.Core.Session;
using SnugWin.Core.Support;
using SnugWin.Windows.Native;

namespace SnugWin.Windows.Sensing;

/// <summary>Reads confirmed RustDesk remote desktop cards from the local CM Flutter MSAA tree.</summary>
public sealed class RustDeskSensor
{
    private const string ProcessName = "RustDesk";
    private const string CmSwitch = "--cm";
    private const string FlutterViewClass = "FLUTTERVIEW";
    private const string FlutterRunnerClass = "FLUTTER_RUNNER_WIN32_WINDOW";
    private const int ObjIdClient = -4;
    private const uint WmGetObject = 0x003D;
    private const int MaxMatchingWindows = 256;
    private const int MaxNodes = 2048;
    private const int MaxDepth = 25;
    private static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ScanBudget = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan WindowMessageTimeout = TimeSpan.FromMilliseconds(250);
    private static readonly Guid IAccessibleId = new("618736e0-3c3d-11cf-810c-00aa00389b71");

    private readonly RustDeskPresenceTracker _tracker = new();
    private readonly Func<bool?> _scanReader;
    private readonly TimeSpan _sampleInterval;
    private readonly TimeSpan _scanTimeout;
    private readonly object _workerLock = new();
    private readonly AutoResetEvent _scanSignal = new(false);
    private Thread? _worker;
    private bool _scanInFlight;
    private bool _scanFinished;
    private bool? _scanResult;
    private Exception? _scanException;
    private long _scanStartedTimestamp;
    private long _lastScanStartedTimestamp;
    private string? _lastLoggedFailureType;

    public RustDeskSensor()
        : this(ReadConnectedCard, SampleInterval, RustDeskPresenceTracker.StaleSampleTimeout)
    {
    }

    internal RustDeskSensor(Func<bool?> scanReader, TimeSpan sampleInterval, TimeSpan scanTimeout)
    {
        _scanReader = scanReader ?? throw new ArgumentNullException(nameof(scanReader));
        _sampleInterval = sampleInterval;
        _scanTimeout = scanTimeout;
    }

    public bool IsConnected => _tracker.IsActive;

    /// <summary>Polls without blocking. Each completed scan is observed at most once.</summary>
    public bool Sample(DateTimeOffset now)
    {
        bool? sample = null;
        Exception? failure = null;
        bool hasCompletedScan = false;
        long timestamp = Stopwatch.GetTimestamp();

        lock (_workerLock)
        {
            if (_scanInFlight && _scanFinished)
            {
                hasCompletedScan = true;
                failure = _scanException;
                sample = failure is null && Stopwatch.GetElapsedTime(_scanStartedTimestamp, timestamp) <= _scanTimeout
                    ? _scanResult
                    : null;
                _scanInFlight = false;
                _scanFinished = false;
                _scanException = null;
                _scanResult = null;
                _scanStartedTimestamp = 0;
            }

            if (!_scanInFlight && (_lastScanStartedTimestamp == 0
                || Stopwatch.GetElapsedTime(_lastScanStartedTimestamp, timestamp) >= _sampleInterval))
            {
                StartScan(timestamp);
            }
        }

        if (failure is not null)
        {
            string typeName = failure.GetType().Name;
            if (!string.Equals(_lastLoggedFailureType, typeName, StringComparison.Ordinal))
            {
                _lastLoggedFailureType = typeName;
                Log.Warn($"RustDesk 无障碍状态读取失败（{typeName}），保留最近状态");
            }
            sample = null;
        }
        else if (hasCompletedScan)
        {
            _lastLoggedFailureType = null;
        }

        if (hasCompletedScan) _tracker.Observe(sample, now);
        _tracker.Expire(now);
        return _tracker.IsActive;
    }

    private void StartScan(long timestamp)
    {
        _scanInFlight = true;
        _scanFinished = false;
        _scanStartedTimestamp = timestamp;
        _lastScanStartedTimestamp = timestamp;
        if (_worker is null)
        {
            _worker = new Thread(ScanWorker)
            {
                IsBackground = true,
                Name = "SnugWin RustDesk accessibility scan"
            };
            _worker.Start();
        }
        _scanSignal.Set();
    }

    private void ScanWorker()
    {
        while (true)
        {
            _scanSignal.WaitOne();
            bool? result = null;
            Exception? exception = null;
            try { result = _scanReader(); }
            catch (Exception ex) { exception = ex; }

            lock (_workerLock)
            {
                _scanResult = result;
                _scanException = exception;
                _scanFinished = true;
            }
        }
    }

    private static bool? ReadConnectedCard()
    {
        int sessionId = Process.GetCurrentProcess().SessionId;
        bool? connectionManager = RustDeskProcessProbe.HasConnectionManagerProcess(sessionId);
        if (connectionManager is not null) return connectionManager;

        // Command lines unreadable: fall back to walking the RustDesk UI tree.
        long start = Stopwatch.GetTimestamp();
        long budget = (long)(Stopwatch.Frequency * ScanBudget.TotalSeconds);
        var processIds = RustDeskProcessProbe.FindSessionProcessIds(sessionId);
        if (processIds.Count == 0) return false;

        var roots = new List<RustDeskAccessibleNode>();
        var inspectedWindows = new HashSet<IntPtr>();
        int matchingWindows = 0;
        bool incomplete = false;
        bool budgetExpired = false;

        foreach (int pid in processIds)
        {
            if (HasBudgetExpired(start, budget)) return null;
            Win32.EnumWindows((hwnd, _) =>
            {
                if (HasBudgetExpired(start, budget))
                {
                    incomplete = true;
                    budgetExpired = true;
                    return false;
                }

                Win32.GetWindowThreadProcessId(hwnd, out int ownerPid);
                if (ownerPid != pid) return true;
                InspectIfFlutterView(hwnd, roots, inspectedWindows, ref matchingWindows, ref incomplete, start, budget);

                bool childEnumerationStopped = false;
                Win32.EnumChildWindows(hwnd, (child, _) =>
                {
                    if (HasBudgetExpired(start, budget))
                    {
                        incomplete = true;
                        childEnumerationStopped = true;
                        return false;
                    }
                    InspectIfFlutterView(child, roots, inspectedWindows, ref matchingWindows, ref incomplete, start, budget);
                    if (matchingWindows > MaxMatchingWindows)
                    {
                        incomplete = true;
                        childEnumerationStopped = true;
                        return false;
                    }
                    return true;
                }, IntPtr.Zero);
                if (childEnumerationStopped)
                {
                    budgetExpired = HasBudgetExpired(start, budget);
                    return false;
                }
                return matchingWindows <= MaxMatchingWindows;
            }, IntPtr.Zero);
            if (budgetExpired || matchingWindows > MaxMatchingWindows) return null;
        }

        if (roots.Count == 0) return matchingWindows == 0 ? false : null;
        var parsed = RustDeskConnectionParser.DetectAuthorizedDesktopConnection(roots);
        if (parsed == true) return true;
        return incomplete ? null : parsed;
    }

    private static bool IsFlutterRoot(IntPtr hwnd)
    {
        string windowClass = Win32.GetWindowClass(hwnd);
        return string.Equals(windowClass, FlutterViewClass, StringComparison.OrdinalIgnoreCase)
            || string.Equals(windowClass, FlutterRunnerClass, StringComparison.OrdinalIgnoreCase);
    }

    private static void InspectIfFlutterView(
        IntPtr hwnd,
        List<RustDeskAccessibleNode> roots,
        HashSet<IntPtr> inspectedWindows,
        ref int matchingWindows,
        ref bool incomplete,
        long start,
        long budget)
    {
        if (!IsFlutterRoot(hwnd)) return;
        if (!inspectedWindows.Add(hwnd)) return;
        matchingWindows++;
        if (matchingWindows > MaxMatchingWindows)
        {
            incomplete = true;
            return;
        }

        if (HasBudgetExpired(start, budget))
        {
            incomplete = true;
            return;
        }

        if (Win32.SendMessageTimeoutW(hwnd, WmGetObject, IntPtr.Zero, new IntPtr(ObjIdClient),
                Win32.SMTO_ABORTIFHUNG, (uint)WindowMessageTimeout.TotalMilliseconds, out IntPtr result) == IntPtr.Zero
            || result == IntPtr.Zero)
        {
            incomplete = true;
            return;
        }

        object? accessible = null;
        try
        {
            Guid iid = IAccessibleId;
            int hr = AccessibleObjectFromWindow(hwnd, ObjIdClient, ref iid, out accessible);
            if (hr < 0 || accessible is not IAccessible root)
            {
                incomplete = true;
                return;
            }

            int nodeCount = 0;
            bool treeComplete = true;
            var snapshot = Snapshot(root, 0, 0, ref nodeCount, start, budget, ref treeComplete);
            if (snapshot is null || !treeComplete) incomplete = true;
            if (snapshot is not null) roots.Add(snapshot);
        }
        catch
        {
            incomplete = true;
        }
        finally
        {
            ReleaseComReference(accessible);
        }
    }

    private static RustDeskAccessibleNode? Snapshot(
        IAccessible accessible,
        object child,
        int depth,
        ref int nodeCount,
        long start,
        long budget,
        ref bool complete)
    {
        if (++nodeCount > MaxNodes || depth > MaxDepth || HasBudgetExpired(start, budget))
        {
            complete = false;
            return null;
        }

        string? name = null;
        try { name = accessible.get_accName(child); } catch { complete = false; }
        int role = 0;
        try
        {
            object? roleValue = accessible.get_accRole(child);
            if (roleValue is not null) role = Convert.ToInt32(roleValue);
        }
        catch { complete = false; }

        int childCount;
        try { childCount = accessible.accChildCount; }
        catch
        {
            complete = false;
            return new RustDeskAccessibleNode(name, Array.Empty<RustDeskAccessibleNode>(), role, false);
        }
        if (childCount < 0 || childCount > MaxNodes)
        {
            complete = false;
            return new RustDeskAccessibleNode(name, Array.Empty<RustDeskAccessibleNode>(), role, false);
        }

        var children = new List<RustDeskAccessibleNode>(Math.Min(childCount, 32));
        if (child is int childId && childId != 0)
        {
            object? nestedObject = null;
            try
            {
                nestedObject = accessible.get_accChild(childId);
                if (nestedObject is IAccessible nested)
                {
                    var nestedNode = Snapshot(nested, 0, depth + 1, ref nodeCount, start, budget, ref complete);
                    if (nestedNode is not null) children.Add(nestedNode);
                }
                else if (nestedObject is null)
                {
                    complete = false;
                }
            }
            catch { complete = false; }
            finally { ReleaseComReference(nestedObject); }
        }
        else if (childCount > 0)
        {
            var objects = new object[childCount];
            try
            {
                int hr = AccessibleChildren(accessible, 0, childCount, objects, out int obtained);
                if (hr < 0 || obtained < childCount) complete = false;
                if (hr >= 0)
                {
                    for (int i = 0; i < Math.Min(obtained, childCount); i++)
                    {
                        object? childObject = objects[i];
                        try
                        {
                            if (childObject is IAccessible nested)
                            {
                                var nestedNode = Snapshot(nested, 0, depth + 1, ref nodeCount, start, budget, ref complete);
                                if (nestedNode is not null) children.Add(nestedNode);
                            }
                            else if (childObject is int id && id != 0)
                            {
                                var childNode = Snapshot(accessible, id, depth + 1, ref nodeCount, start, budget, ref complete);
                                if (childNode is not null) children.Add(childNode);
                            }
                            else
                            {
                                complete = false;
                            }
                        }
                        catch { complete = false; }
                        finally { ReleaseComReference(childObject); }
                        if (nodeCount >= MaxNodes || HasBudgetExpired(start, budget))
                        {
                            complete = false;
                            break;
                        }
                    }
                }
            }
            catch { complete = false; }
        }

        return new RustDeskAccessibleNode(name, children, role, complete);
    }

    private static bool HasBudgetExpired(long start, long budget) => Stopwatch.GetTimestamp() - start > budget;

    private static void ReleaseComReference(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            try { Marshal.ReleaseComObject(value); } catch { }
        }
    }

    [DllImport("oleacc.dll", EntryPoint = "AccessibleObjectFromWindow")]
    private static extern int AccessibleObjectFromWindow(
        IntPtr hwnd,
        int objectId,
        ref Guid interfaceId,
        [MarshalAs(UnmanagedType.Interface)] out object? accessibleObject);

    [DllImport("oleacc.dll", EntryPoint = "AccessibleChildren")]
    private static extern int AccessibleChildren(
        IAccessible container,
        int childStart,
        int childCount,
        [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 2)] object[] children,
        out int obtained);
}
