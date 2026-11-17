using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using SnugWin.Core.Session;
using SnugWin.Windows.Native;
using SnugWin.Windows.Windows;

namespace SnugWin.Windows.Sensing;

/// <summary>
/// Observes NetEase UU Remote (GameViewer) on Windows via multiple local signals:
/// 1. Visible windows owned by GameViewerServer (if floating ball/overlay is enabled);
/// 2. UI Automation inspection of GameViewer main window ("本机被控中" controller list);
/// 3. Active non-443/non-80 TCP peer connections on GameViewerServer (e.g. LAN/relay stream)
///    or sustained GameViewerServer CPU encoding activity;
/// 4. Active byte growth of GameViewerServer streamer log (streamer_log_controlled_*_<pid>.slog).
/// </summary>
public sealed class UUSensor
{
    private static readonly string ServerLogDir = @"C:\Program Files\Netease\GameViewer\log\server\log";
    private DateTimeOffset _prevLogSizeAdvance = DateTimeOffset.MinValue;
    private DateTimeOffset _lastLogSizeAdvance = DateTimeOffset.MinValue;
    private long _lastObservedLogBytes = -1;
    private int _lastServerPid;

    public bool ManualOverride { get; set; }

    public UUSignals Sample(DateTimeOffset now)
    {
        if (ManualOverride)
        {
            return new UUSignals(
                OverlayVisible: true,
                Controllers: ControllerTable.FromNames("手动开启 (Phone)"),
                CaptureActive: true);
        }

        int? serverPid = FindProcessPid("GameViewerServer");
        int? clientPid = FindProcessPid("GameViewer");

        bool overlayVisible = serverPid is { } sp && HasVisibleWindow(sp);
        var controllers = clientPid is { } cp ? ReadControllers(cp) : ControllerTable.Unreadable;
        bool? captureActive = SampleCaptureActive(serverPid, now);

        return new UUSignals(overlayVisible, controllers, captureActive);
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
            _lastObservedLogBytes = -1;
            _prevLogSizeAdvance = DateTimeOffset.MinValue;
            _lastLogSizeAdvance = DateTimeOffset.MinValue;
            return false;
        }

        if (pid != _lastServerPid)
        {
            _lastServerPid = pid;
            _lastObservedLogBytes = -1;
            _prevLogSizeAdvance = DateTimeOffset.MinValue;
            _lastLogSizeAdvance = DateTimeOffset.MinValue;
        }

        // Signal A: GameViewerServer has an established non-443/non-80 peer TCP connection (e.g. port 2533/2091)
        if (HasActivePeerTcpConnection(pid))
            return true;

        // Signal B: continuous byte growth of streamer_log_controlled_*_<pid>.slog (>= 2 writes within 3s, max 2s hold)
        // A single isolated log flush (idle heartbeat / post-disconnect stats flush) must NOT trigger a phantom session.
        if (Directory.Exists(ServerLogDir))
        {
            try
            {
                var files = Directory.EnumerateFiles(ServerLogDir, $"streamer_log_controlled_*_{pid}.slog")
                    .Select(p => new FileInfo(p))
                    .ToList();
                if (files.Count > 0)
                {
                    long totalBytes = files.Sum(f => f.Length);
                    if (_lastObservedLogBytes < 0)
                    {
                        _lastObservedLogBytes = totalBytes;
                    }
                    else if (totalBytes > _lastObservedLogBytes)
                    {
                        _lastObservedLogBytes = totalBytes;
                        _prevLogSizeAdvance = _lastLogSizeAdvance;
                        _lastLogSizeAdvance = now;
                    }

                    bool continuous = _prevLogSizeAdvance != DateTimeOffset.MinValue
                                      && (_lastLogSizeAdvance - _prevLogSizeAdvance).TotalSeconds <= 3.0;
                    if (continuous && (now - _lastLogSizeAdvance).TotalSeconds < 2.0)
                        return true;
                }
            }
            catch
            {
                // ignore log directory error
            }
        }

        return false;
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
