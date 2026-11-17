using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using SnugWin.Core.PhoneScreen;
using SnugWin.Core.Support;
using SnugWin.Windows.Native;

namespace SnugWin.Windows.Display;

public readonly record struct AdapterLuid(uint LowPart, int HighPart);

public sealed record MonitorSnapshot(
    uint StableId,
    string DeviceName,
    string FriendlyName,
    string DeviceString,
    RectD Bounds,
    RectD WorkArea,
    bool IsPrimary,
    DisplayModeInfo Mode,
    AdapterLuid AdapterId,
    uint SourceId);

public static unsafe class DisplayConfigService
{
    private static readonly int[] DpiScaleTable = [100, 125, 150, 175, 200, 225, 250, 300, 350, 400, 450, 500];

    public static IReadOnlyList<MonitorSnapshot> GetActiveMonitors()
    {
        var ccdSources = QueryCcdSources();
        var deviceStrings = QueryDeviceStrings();
        var results = new List<MonitorSnapshot>();

        var handle = GCHandle.Alloc(results);
        try
        {
            Win32.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMonitor, IntPtr _, ref Win32.RECT _, IntPtr data) =>
            {
                var list = (List<MonitorSnapshot>)GCHandle.FromIntPtr(data).Target!;
                var mi = new Win32.MONITORINFOEX { cbSize = (uint)Marshal.SizeOf<Win32.MONITORINFOEX>() };
                if (!Win32.GetMonitorInfoW(hMonitor, ref mi)) return true;

                string devName = new string(mi.szDevice).TrimEnd('\0');
                var bounds = new RectD(mi.rcMonitor.Left, mi.rcMonitor.Top, mi.rcMonitor.Width, mi.rcMonitor.Height);
                var work = new RectD(mi.rcWork.Left, mi.rcWork.Top, mi.rcWork.Width, mi.rcWork.Height);
                bool primary = (mi.dwFlags & Win32.MONITORINFOF_PRIMARY) != 0;

                var dm = new Win32.DEVMODE { dmSize = (ushort)Marshal.SizeOf<Win32.DEVMODE>() };
                int width = (int)bounds.Width;
                int height = (int)bounds.Height;
                double hz = 60.0;
                if (Win32.EnumDisplaySettingsExW(devName, Win32.ENUM_CURRENT_SETTINGS, ref dm, 0))
                {
                    width = (int)dm.dmPelsWidth;
                    height = (int)dm.dmPelsHeight;
                    if (dm.dmDisplayFrequency > 0) hz = dm.dmDisplayFrequency;
                }

                ccdSources.TryGetValue(devName, out var ccd);
                var adapterLuid = new AdapterLuid(ccd.AdapterId.LowPart, ccd.AdapterId.HighPart);
                int dpi = ccd.Found ? GetDpiScalePercent(adapterLuid, ccd.SourceId) : 100;
                string friendly = ccd.Found && !string.IsNullOrWhiteSpace(ccd.FriendlyName)
                    ? ccd.FriendlyName
                    : devName;
                deviceStrings.TryGetValue(devName, out var devStr);

                uint stableId = ComputeStableId(devName, ccd.AdapterId, ccd.SourceId);
                list.Add(new MonitorSnapshot(
                    stableId,
                    devName,
                    friendly,
                    devStr ?? string.Empty,
                    bounds,
                    work,
                    primary,
                    new DisplayModeInfo(width, height, hz, dpi),
                    adapterLuid,
                    ccd.SourceId));
                return true;
            }, GCHandle.ToIntPtr(handle));
        }
        finally
        {
            handle.Free();
        }

        return results;
    }

    public static bool SetResolution(string deviceName, int pixelWidth, int pixelHeight, double refreshRate = 60)
    {
        var dm = new Win32.DEVMODE { dmSize = (ushort)Marshal.SizeOf<Win32.DEVMODE>() };
        if (!Win32.EnumDisplaySettingsExW(deviceName, Win32.ENUM_CURRENT_SETTINGS, ref dm, 0))
            return false;

        dm.dmPelsWidth = (uint)pixelWidth;
        dm.dmPelsHeight = (uint)pixelHeight;
        dm.dmFields = Win32.DM_PELSWIDTH | Win32.DM_PELSHEIGHT;
        if (refreshRate > 0)
        {
            dm.dmDisplayFrequency = (uint)Math.Round(refreshRate);
            dm.dmFields |= Win32.DM_DISPLAYFREQUENCY;
        }

        int res = Win32.ChangeDisplaySettingsExW(deviceName, ref dm, IntPtr.Zero, Win32.CDS_UPDATEREGISTRY, IntPtr.Zero);
        if (res != Win32.DISP_CHANGE_SUCCESSFUL)
        {
            Log.Warn($"ChangeDisplaySettingsEx({deviceName}, {pixelWidth}x{pixelHeight}) 返回 {res}");
            return false;
        }
        return true;
    }

    public static bool TryAttachVirtualDisplay(int pixelWidth, int pixelHeight, int posX, int posY, out string? attachedDeviceName)
    {
        attachedDeviceName = null;
        var dd = new Win32.DISPLAY_DEVICE { cb = (uint)Marshal.SizeOf<Win32.DISPLAY_DEVICE>() };
        uint i = 0;
        while (Win32.EnumDisplayDevicesW(null, i++, ref dd, 0))
        {
            string devName = new string(dd.DeviceName).TrimEnd('\0');
            string devStr = new string(dd.DeviceString).TrimEnd('\0');
            if (!devStr.Contains("Virtual Display", StringComparison.OrdinalIgnoreCase)) continue;
            if ((dd.StateFlags & Win32.DISPLAY_DEVICE_ACTIVE) != 0) continue;

            var mon = new Win32.DISPLAY_DEVICE { cb = (uint)Marshal.SizeOf<Win32.DISPLAY_DEVICE>() };
            if (!Win32.EnumDisplayDevicesW(devName, 0, ref mon, 0)) continue;

            string monStr = new string(mon.DeviceString).TrimEnd('\0');
            Log.Info($"发现已到达但未启用的虚拟屏：{devName} ({monStr}, flags=0x{mon.StateFlags:X})，尝试扩展桌面...");

            var dm = new Win32.DEVMODE
            {
                dmSize = (ushort)Marshal.SizeOf<Win32.DEVMODE>(),
                dmFields = Win32.DM_POSITION | Win32.DM_PELSWIDTH | Win32.DM_PELSHEIGHT | Win32.DM_BITSPERPEL | Win32.DM_DISPLAYFREQUENCY,
                dmPositionX = posX,
                dmPositionY = posY,
                dmBitsPerPel = 32,
                dmPelsWidth = (uint)pixelWidth,
                dmPelsHeight = (uint)pixelHeight,
                dmDisplayFrequency = 60
            };

            int r1 = Win32.ChangeDisplaySettingsExW(devName, ref dm, IntPtr.Zero, Win32.CDS_UPDATEREGISTRY | Win32.CDS_NORESET, IntPtr.Zero);
            int r2 = Win32.ChangeDisplaySettingsExW(null, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero);
            Log.Info($"ChangeDisplaySettingsEx({devName}, {pixelWidth}x{pixelHeight}) => r1={r1}, r2={r2}");
            if (r1 == Win32.DISP_CHANGE_SUCCESSFUL && r2 == Win32.DISP_CHANGE_SUCCESSFUL)
            {
                attachedDeviceName = devName;
                return true;
            }

            int sdc = Win32.SetDisplayConfig(0, IntPtr.Zero, 0, IntPtr.Zero, Win32.SDC_APPLY | Win32.SDC_TOPOLOGY_EXTEND);
            Log.Info($"SetDisplayConfig(SDC_TOPOLOGY_EXTEND) => {sdc}");
            if (sdc == 0)
            {
                attachedDeviceName = devName;
                return true;
            }
        }
        return false;
    }

    public static bool HasDisplayMode(string deviceName, int pixelWidth, int pixelHeight)
    {
        var dm = new Win32.DEVMODE { dmSize = (ushort)Marshal.SizeOf<Win32.DEVMODE>() };
        int idx = 0;
        while (Win32.EnumDisplaySettingsExW(deviceName, idx++, ref dm, 0))
        {
            if (dm.dmPelsWidth == (uint)pixelWidth && dm.dmPelsHeight == (uint)pixelHeight)
                return true;
        }
        return false;
    }

    public static int GetDpiScalePercent(AdapterLuid adapterLuid, uint sourceId)
    {
        var adapterId = new Win32.LUID { LowPart = adapterLuid.LowPart, HighPart = adapterLuid.HighPart };
        var pkt = new Win32.DISPLAYCONFIG_SOURCE_DPI_SCALE_GET
        {
            header = new Win32.DISPLAYCONFIG_DEVICE_INFO_HEADER
            {
                type = Win32.DISPLAYCONFIG_DEVICE_INFO_GET_DPI_SCALE,
                size = (uint)Marshal.SizeOf<Win32.DISPLAYCONFIG_SOURCE_DPI_SCALE_GET>(),
                adapterId = adapterId,
                id = sourceId
            }
        };
        if (Win32.DisplayConfigGetDeviceInfo(ref pkt) != 0) return 100;

        int recommendedIdx = Math.Clamp(-pkt.minScaleRel, 0, DpiScaleTable.Length - 1);
        int curIdx = Math.Clamp(recommendedIdx + pkt.curScaleRel, 0, DpiScaleTable.Length - 1);
        return DpiScaleTable[curIdx];
    }

    public static bool SetDpiScalePercent(AdapterLuid adapterLuid, uint sourceId, int targetPercent)
    {
        var adapterId = new Win32.LUID { LowPart = adapterLuid.LowPart, HighPart = adapterLuid.HighPart };
        var getPkt = new Win32.DISPLAYCONFIG_SOURCE_DPI_SCALE_GET
        {
            header = new Win32.DISPLAYCONFIG_DEVICE_INFO_HEADER
            {
                type = Win32.DISPLAYCONFIG_DEVICE_INFO_GET_DPI_SCALE,
                size = (uint)Marshal.SizeOf<Win32.DISPLAYCONFIG_SOURCE_DPI_SCALE_GET>(),
                adapterId = adapterId,
                id = sourceId
            }
        };
        if (Win32.DisplayConfigGetDeviceInfo(ref getPkt) != 0)
            return false;

        int recommendedIdx = Math.Clamp(-getPkt.minScaleRel, 0, DpiScaleTable.Length - 1);
        int targetIdx = Array.IndexOf(DpiScaleTable, targetPercent);
        if (targetIdx < 0) return false;

        int rel = Math.Clamp(targetIdx - recommendedIdx, getPkt.minScaleRel, getPkt.maxScaleRel);
        if (rel == getPkt.curScaleRel) return true;

        var setPkt = new Win32.DISPLAYCONFIG_SOURCE_DPI_SCALE_SET
        {
            header = new Win32.DISPLAYCONFIG_DEVICE_INFO_HEADER
            {
                type = Win32.DISPLAYCONFIG_DEVICE_INFO_SET_DPI_SCALE,
                size = (uint)Marshal.SizeOf<Win32.DISPLAYCONFIG_SOURCE_DPI_SCALE_SET>(),
                adapterId = adapterId,
                id = sourceId
            },
            scaleRel = rel
        };
        int status = Win32.DisplayConfigSetDeviceInfo(ref setPkt);
        if (status != 0)
        {
            Log.Warn($"设置 DPI 缩放 {targetPercent}% 失败（status={status}）");
            return false;
        }
        return true;
    }

    private static Dictionary<string, string> QueryDeviceStrings()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var dd = new Win32.DISPLAY_DEVICE { cb = (uint)Marshal.SizeOf<Win32.DISPLAY_DEVICE>() };
        uint i = 0;
        while (Win32.EnumDisplayDevicesW(null, i++, ref dd, 0))
        {
            string name = new string(dd.DeviceName).TrimEnd('\0');
            string desc = new string(dd.DeviceString).TrimEnd('\0');
            if (!string.IsNullOrEmpty(name))
                map[name] = desc;
        }
        return map;
    }

    private static Dictionary<string, (bool Found, Win32.LUID AdapterId, uint SourceId, string FriendlyName)> QueryCcdSources()
    {
        var map = new Dictionary<string, (bool, Win32.LUID, uint, string)>(StringComparer.OrdinalIgnoreCase);
        if (Win32.GetDisplayConfigBufferSizes(Win32.QDC_ONLY_ACTIVE_PATHS, out uint pathCount, out uint modeCount) != 0)
            return map;

        var paths = new Win32.DISPLAYCONFIG_PATH_INFO[pathCount];
        var modes = new Win32.DISPLAYCONFIG_MODE_INFO[modeCount];
        if (Win32.QueryDisplayConfig(Win32.QDC_ONLY_ACTIVE_PATHS, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero) != 0)
            return map;

        for (int i = 0; i < pathCount; i++)
        {
            var p = paths[i];
            var src = new Win32.DISPLAYCONFIG_SOURCE_DEVICE_NAME
            {
                header = new Win32.DISPLAYCONFIG_DEVICE_INFO_HEADER
                {
                    type = Win32.DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME,
                    size = (uint)Marshal.SizeOf<Win32.DISPLAYCONFIG_SOURCE_DEVICE_NAME>(),
                    adapterId = p.sourceInfo.adapterId,
                    id = p.sourceInfo.id
                }
            };
            if (Win32.DisplayConfigGetDeviceInfo(ref src) != 0) continue;
            string gdiName = new string(src.viewGdiDeviceName).TrimEnd('\0');
            if (string.IsNullOrEmpty(gdiName)) continue;

            var tgt = new Win32.DISPLAYCONFIG_TARGET_DEVICE_NAME
            {
                header = new Win32.DISPLAYCONFIG_DEVICE_INFO_HEADER
                {
                    type = Win32.DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME,
                    size = (uint)Marshal.SizeOf<Win32.DISPLAYCONFIG_TARGET_DEVICE_NAME>(),
                    adapterId = p.targetInfo.adapterId,
                    id = p.targetInfo.id
                }
            };
            string friendly = Win32.DisplayConfigGetDeviceInfo(ref tgt) == 0
                ? new string(tgt.monitorFriendlyDeviceName).TrimEnd('\0')
                : gdiName;

            if (!map.ContainsKey(gdiName))
                map[gdiName] = (true, p.sourceInfo.adapterId, p.sourceInfo.id, friendly);
        }

        return map;
    }

    private static uint ComputeStableId(string devName, Win32.LUID adapterId, uint sourceId)
    {
        if (adapterId.LowPart != 0 || adapterId.HighPart != 0)
            return (uint)HashCode.Combine(adapterId.LowPart, adapterId.HighPart, sourceId);
        return (uint)StringComparer.OrdinalIgnoreCase.GetHashCode(devName);
    }
}
