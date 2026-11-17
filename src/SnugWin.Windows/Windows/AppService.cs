using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.Win32;
using SnugWin.Core.PhoneScreen;
using SnugWin.Core.Support;
using SnugWin.Windows.Native;

namespace SnugWin.Windows.Windows;

public sealed class AppService : IAppControl
{
    private readonly WindowService _windows;

    public AppService(WindowService windows)
    {
        _windows = windows;
    }

    public Task<int?> PidOfAsync(AppInfo app)
    {
        string targetExe = Path.GetFileNameWithoutExtension(app.ExecutablePath ?? app.AppId);
        var procs = Process.GetProcessesByName(targetExe);
        try
        {
            var withWin = procs.FirstOrDefault(p => p.MainWindowHandle != IntPtr.Zero) ?? procs.FirstOrDefault();
            return Task.FromResult<int?>(withWin?.Id);
        }
        finally
        {
            foreach (var p in procs) p.Dispose();
        }
    }

    public Task<IReadOnlyList<int>> PidsAsync(string appId)
    {
        string name = Path.GetFileNameWithoutExtension(appId);
        var procs = Process.GetProcessesByName(name);
        try
        {
            return Task.FromResult<IReadOnlyList<int>>(procs.Select(p => p.Id).ToList());
        }
        finally
        {
            foreach (var p in procs) p.Dispose();
        }
    }

    public Task<IReadOnlyList<WindowInfo>> AppWindowsAsync(AppInfo app, int? launchedPid = null)
    {
        string targetExe = Path.GetFileName(app.ExecutablePath ?? app.AppId);
        string? targetDir = !string.IsNullOrEmpty(app.ExecutablePath) ? Path.GetDirectoryName(app.ExecutablePath) : null;
        var all = WindowService.EnumerateStandardWindows(pidFilter: null, includeMinimized: false);
        var matched = all.Where(w =>
        {
            if (launchedPid is { } lp && w.Key.Pid == lp) return true;
            if (!string.IsNullOrEmpty(w.AppId) && w.AppId.Equals(targetExe, StringComparison.OrdinalIgnoreCase))
                return true;
            if (!string.IsNullOrEmpty(targetDir))
            {
                string? img = Win32.GetProcessImagePath(w.Key.Pid);
                if (!string.IsNullOrEmpty(img) && img.StartsWith(targetDir, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }).ToList();
        return Task.FromResult<IReadOnlyList<WindowInfo>>(matched);
    }

    public Task<int?> OpenAsync(AppInfo app)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = app.LaunchTarget ?? app.ExecutablePath ?? app.AppId,
                Arguments = app.Arguments ?? string.Empty,
                WorkingDirectory = app.WorkingDirectory ?? string.Empty,
                UseShellExecute = true
            };
            using var proc = Process.Start(psi);
            if (proc is not null && !proc.HasExited)
                return Task.FromResult<int?>(proc.Id);

            // Launcher/shell-execute might hand off to existing process; find by exe name
            string exeName = Path.GetFileNameWithoutExtension(app.ExecutablePath ?? app.AppId);
            var found = Process.GetProcessesByName(exeName).FirstOrDefault();
            return Task.FromResult<int?>(found?.Id);
        }
        catch (Exception ex)
        {
            Log.Warn($"启动 {app.Name} 失败：{ex.Message}");
            return Task.FromResult<int?>(null);
        }
    }

    public async Task<bool> NewWindowAsync(int pid)
    {
        var shown = await _windows.ShownWindowsAsync(pid).ConfigureAwait(false);
        var target = shown.FirstOrDefault();
        if (target is null) return false;

        var hwnd = (IntPtr)target.Key.Handle;
        await _windows.FocusAsync(target.Key).ConfigureAwait(false);
        await Task.Delay(80).ConfigureAwait(false);

        if (UiaHelper.TryInvokeNewWindowMenu(hwnd))
            return true;

        string? appId = target.AppId;
        bool shiftN = appId is not null && (
            appId.Equals("WindowsTerminal.exe", StringComparison.OrdinalIgnoreCase)
            || appId.Equals("wt.exe", StringComparison.OrdinalIgnoreCase)
            || appId.Equals("Code.exe", StringComparison.OrdinalIgnoreCase)
            || appId.Equals("Cursor.exe", StringComparison.OrdinalIgnoreCase)
            || appId.Equals("Windsurf.exe", StringComparison.OrdinalIgnoreCase)
            || appId.Equals("Zed.exe", StringComparison.OrdinalIgnoreCase));

        WindowService.SendCtrlShortcut(Win32.VK_N, withShift: shiftN);
        return true;
    }

    public async Task<bool> QuitAsync(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            if (p.HasExited) return true;
            if (p.MainWindowHandle != IntPtr.Zero)
                p.CloseMainWindow();
            else
            {
                var windows = await _windows.ShownWindowsAsync(pid).ConfigureAwait(false);
                foreach (var w in windows)
                    Win32.PostMessageW((IntPtr)w.Key.Handle, Win32.WM_CLOSE, 0, 0);
            }
            return true;
        }
        catch
        {
            return true;
        }
    }

    public Task<bool> ForceQuitAsync(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            if (!p.HasExited) p.Kill(entireProcessTree: true);
            return Task.FromResult(true);
        }
        catch
        {
            return Task.FromResult(false);
        }
    }

    public Task<bool> IsRunningAsync(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return Task.FromResult(!p.HasExited);
        }
        catch
        {
            return Task.FromResult(false);
        }
    }

    public IReadOnlyList<RunningApp> GetRunningApps()
    {
        var allWindows = WindowService.EnumerateStandardWindows(pidFilter: null, includeMinimized: true);
        var zOrder = WindowService.GetOnScreenWindowZOrder().Select(x => x.Handle).ToList();
        var byApp = allWindows.GroupBy(w => w.AppId ?? w.Key.Pid.ToString(), StringComparer.OrdinalIgnoreCase);
        var list = new List<RunningApp>();
        foreach (var grp in byApp)
        {
            var first = grp.First();
            string? path = Win32.GetProcessImagePath(first.Key.Pid);
            var appInfo = new AppInfo(
                first.AppId ?? first.AppName,
                first.AppName,
                path);
            list.Add(new RunningApp(appInfo, first.Key.Pid, grp.ToList()));
        }
        return AppList.Ordered(list, zOrder);
    }

    public static IReadOnlyList<AppInfo> ScanInstalledApps()
    {
        var map = new Dictionary<string, AppInfo>(StringComparer.OrdinalIgnoreCase);

        // 1. Scan Start Menu shortcuts (.lnk)
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
            Environment.GetFolderPath(Environment.SpecialFolder.Programs)
        };

        foreach (string root in roots)
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) continue;
            IEnumerable<string> links;
            try
            {
                links = Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories);
            }
            catch
            {
                continue;
            }

            foreach (string lnk in links)
            {
                string name = Path.GetFileNameWithoutExtension(lnk);
                if (IsIgnoredShortcutName(name)) continue;

                string? target = ResolveShortcutTarget(lnk);
                if (string.IsNullOrEmpty(target) || !target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!File.Exists(target)) continue;

                string exe = Path.GetFileName(target);
                if (WindowService.IsExcludedApp(exe) || IsInstallerOrHelper(exe, name)) continue;

                if (!map.ContainsKey(exe))
                {
                    map[exe] = new AppInfo(exe, name, target, lnk);
                }
            }
        }

        // 2. Scan Registry App Paths (HKLM & HKCU)
        ScanAppPaths(Registry.LocalMachine, map);
        ScanAppPaths(Registry.CurrentUser, map);

        return AppList.Sorted(map.Values);
    }

    private static void ScanAppPaths(RegistryKey root, Dictionary<string, AppInfo> map)
    {
        try
        {
            using var key = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths");
            if (key is null) return;
            foreach (string sub in key.GetSubKeyNames())
            {
                if (!sub.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                if (WindowService.IsExcludedApp(sub) || IsInstallerOrHelper(sub, sub)) continue;
                using var sk = key.OpenSubKey(sub);
                string? rawPath = sk?.GetValue(null)?.ToString()?.Trim('"');
                if (string.IsNullOrEmpty(rawPath) || !File.Exists(rawPath)) continue;
                if (map.ContainsKey(sub)) continue;

                string name = Path.GetFileNameWithoutExtension(rawPath);
                try
                {
                    var vi = FileVersionInfo.GetVersionInfo(rawPath);
                    if (!string.IsNullOrWhiteSpace(vi.FileDescription)) name = vi.FileDescription.Trim();
                }
                catch { }
                map[sub] = new AppInfo(sub, name, rawPath, rawPath);
            }
        }
        catch
        {
            // ignore registry access errors
        }
    }

    private static bool IsIgnoredShortcutName(string name) =>
        name.Contains("卸载", StringComparison.Ordinal)
        || name.Contains("Uninstall", StringComparison.OrdinalIgnoreCase)
        || name.Contains("帮助", StringComparison.Ordinal)
        || name.Contains("Help", StringComparison.OrdinalIgnoreCase)
        || name.Contains("Readme", StringComparison.OrdinalIgnoreCase)
        || name.Contains("Website", StringComparison.OrdinalIgnoreCase);

    private static bool IsInstallerOrHelper(string exe, string name) =>
        exe.Contains("unins", StringComparison.OrdinalIgnoreCase)
        || exe.Contains("setup", StringComparison.OrdinalIgnoreCase)
        || exe.Contains("update", StringComparison.OrdinalIgnoreCase)
        || name.Equals("SnugWin", StringComparison.OrdinalIgnoreCase);

    private static string? ResolveShortcutTarget(string lnkPath)
    {
        try
        {
            var t = Type.GetTypeFromProgID("WScript.Shell");
            if (t is null) return null;
            dynamic? shell = Activator.CreateInstance(t);
            if (shell is null) return null;
            try
            {
                dynamic shortcut = shell.CreateShortcut(lnkPath);
                string? target = shortcut.TargetPath as string;
                Marshal.FinalReleaseComObject(shortcut);
                return target;
            }
            finally
            {
                Marshal.FinalReleaseComObject(shell);
            }
        }
        catch
        {
            return null;
        }
    }
}
