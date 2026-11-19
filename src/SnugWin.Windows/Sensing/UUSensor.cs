using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using SnugWin.Core.Session;
using SnugWin.Core.Support;
using SnugWin.Windows.Native;
using SnugWin.Windows.Windows;

namespace SnugWin.Windows.Sensing;

/// <summary>
/// Observes NetEase UU Remote (GameViewer) on Windows via local signals:
/// 1. Visible windows owned by GameViewerServer (if floating ball/overlay is enabled);
/// 2. UI Automation inspection of GameViewer main window ("本机被控中" controller list);
/// 3. Continuous byte growth of GameViewerServer streamer log (streamer_log_controlled_*_<pid>.slog);
/// Diagnostic context only: active non-443/non-80 TCP peer sockets on GameViewerServer (never triggers presence alone).
/// </summary>
public sealed class UUSensor
{
    private readonly StreamLogActivityTracker _streamLog = new();
    private readonly UUProcessWatch _serverWatch;
    private readonly UUProcessWatch _clientWatch;
    private int _lastServerPid;
    private string? _resolvedLogDir;
    private bool _loggedLogReadError;

    public UUSensor()
    {
        _serverWatch = new UUProcessWatch(() => FindProcessPid("GameViewerServer"), IsProcessAlive);
        _clientWatch = new UUProcessWatch(() => FindProcessPid("GameViewer"), IsProcessAlive);
    }

    public bool ManualOverride { get; set; }
    public bool LastPeerTcpObserved { get; private set; }

    public UUSignals Sample(DateTimeOffset now)
    {
        if (ManualOverride)
        {
            return new UUSignals(
                OverlayVisible: true,
                Controllers: ControllerTable.FromNames("手动开启 (Phone)"),
                CaptureActive: true);
        }

        int? serverPid = _serverWatch.Resolve(now);
        int? clientPid = _clientWatch.Resolve(now);

        bool overlayVisible = serverPid is { } sp && HasVisibleWindow(sp);
        var controllers = clientPid is { } cp ? ReadControllers(cp) : ControllerTable.Unreadable;
        bool? captureActive = SampleCaptureActive(serverPid, now);

        return new UUSignals(overlayVisible, controllers, captureActive);
    }

    private static bool IsProcessAlive(int pid)
    {
        try
        {
            using var proc = Process.GetProcessById(pid);
            return !proc.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private static int? FindProcessPid(string processName)
    {
        var procs = Process.GetProcessesByName(processName);
        try
        {
            return procs.Length > 0 ? procs[0].Id : null;
        }
        finally
        {
            foreach (var p in procs) p.Dispose();
        }
    }

    private static bool HasVisibleWindow(int pid)
    {
        bool found = false;
        Win32.EnumWindows((hwnd, _) =>
        {
            if (!Win32.IsWindowVisible(hwnd)) return true;
            Win32.GetWindowThreadProcessId(hwnd, out int wPid);
            if (wPid != pid) return true;
            if (Win32.GetWindowRect(hwnd, out var rc) && rc.Width > 10 && rc.Height > 10)
            {
                found = true;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    private static ControllerTable ReadControllers(int clientPid)
    {
        var hwnds = new List<IntPtr>();
        Win32.EnumWindows((hwnd, _) =>
        {
            if (!Win32.IsWindowVisible(hwnd)) return true;
            Win32.GetWindowThreadProcessId(hwnd, out int wPid);
            if (wPid != clientPid) return true;
            hwnds.Add(hwnd);
            return true;
        }, IntPtr.Zero);

        if (hwnds.Count == 0) return ControllerTable.Unreadable;
        return UiaHelper.ReadGameViewerControllers(hwnds);
    }

    private bool? SampleCaptureActive(int? serverPid, DateTimeOffset now)
    {
        if (serverPid is not { } pid)
        {
            _lastServerPid = 0;
            _resolvedLogDir = null;
            LastPeerTcpObserved = false;
            return _streamLog.Observe(null, null, false, now);
        }

        if (pid != _lastServerPid)
        {
            _lastServerPid = pid;
            _resolvedLogDir = ResolveServerLogDir(pid);
            _loggedLogReadError = false;
        }

        // TCP peer connections are recorded for diagnostics only; never treat lingering control/keepalive sockets as active streaming.
        LastPeerTcpObserved = HasActivePeerTcpConnection(pid);

        string? logDir = _resolvedLogDir ??= ResolveServerLogDir(pid);
        if (string.IsNullOrEmpty(logDir) || !Directory.Exists(logDir))
            return _streamLog.Observe(pid, null, false, now);

        try
        {
            var files = Directory.EnumerateFiles(logDir, $"streamer_log_controlled_*_{pid}.slog")
                .Select(p => new FileInfo(p))
                .ToList();
            if (files.Count == 0)
                return _streamLog.Observe(pid, null, false, now);

            long totalBytes = files.Sum(f => f.Length);
            return _streamLog.Observe(pid, totalBytes, true, now);
        }
        catch (Exception ex)
        {
            if (!_loggedLogReadError)
            {
                _loggedLogReadError = true;
                Log.Warn($"读取 UU 推流日志目录失败（{logDir}）：{ex.Message}");
            }
            return _streamLog.Observe(pid, null, false, now);
        }
    }

    private static string? ResolveServerLogDir(int serverPid)
    {
        var candidates = new List<string>();
        string? serverExe = Win32.GetProcessImagePath(serverPid);
        if (!string.IsNullOrEmpty(serverExe))
        {
            string? serverDir = Path.GetDirectoryName(serverExe);
            if (!string.IsNullOrEmpty(serverDir))
            {
                candidates.Add(Path.Combine(serverDir, "log"));
                string? parentDir = Directory.GetParent(serverDir)?.FullName;
                if (!string.IsNullOrEmpty(parentDir))
                {
                    candidates.Add(Path.Combine(parentDir, "log", "server", "log"));
                    candidates.Add(Path.Combine(parentDir, "log"));
                }
            }
        }

        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrEmpty(programFiles))
            candidates.Add(Path.Combine(programFiles, "Netease", "GameViewer", "log", "server", "log"));
        candidates.Add(@"C:\Program Files\Netease\GameViewer\log\server\log");
        candidates.Add(@"D:\Program Files\Netease\GameViewer\log\server\log");
        candidates.Add(@"D:\GameViewer\log\server\log");

        return candidates.FirstOrDefault(Directory.Exists);
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(
        IntPtr pTcpTable,
        ref int pdwSize,
        bool bOrder,
        int ulAf,
        int tableClass,
        uint reserved);

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_TCPROW_OWNER_PID
    {
        public uint dwState;
        public uint dwLocalAddr;
        public uint dwLocalPort;
        public uint dwRemoteAddr;
        public uint dwRemotePort;
        public int dwOwningPid;
    }

    private static bool HasActivePeerTcpConnection(int pid)
    {
        const int AF_INET = 2;
        const int TCP_TABLE_OWNER_PID_ALL = 5;
        const uint MIB_TCP_STATE_ESTAB = 5;

        int size = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0);
        if (size <= 0) return false;

        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(buffer, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0) != 0)
                return false;

            int count = Marshal.ReadInt32(buffer);
            IntPtr rowPtr = buffer + 4;
            int rowSize = Marshal.SizeOf<MIB_TCPROW_OWNER_PID>();
            for (int i = 0; i < count; i++)
            {
                var row = Marshal.PtrToStructure<MIB_TCPROW_OWNER_PID>(rowPtr);
                rowPtr += rowSize;
                if (row.dwOwningPid != pid || row.dwState != MIB_TCP_STATE_ESTAB) continue;

                int remotePort = ((int)(row.dwRemotePort & 0xFF) << 8) | ((int)(row.dwRemotePort >> 8) & 0xFF);
                bool isLoopback = (row.dwRemoteAddr & 0xFF) == 127;
                if (!isLoopback && remotePort != 443 && remotePort != 80 && remotePort != 0)
                    return true;
            }
        }
        catch
        {
            // ignore
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
        return false;
    }
}
