using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using SnugWin.Core.Support;

namespace SnugWin.Windows.Support;

public static class StartupTaskService
{
    public static bool IsEnabled()
    {
        var (code, _) = RunSchtasks($"/Query /TN \"{StartupTaskDefinition.TaskName}\"");
        return code == 0;
    }

    public static void RefreshPathIfEnabled()
    {
        try
        {
            var (code, xml) = RunSchtasks($"/Query /TN \"{StartupTaskDefinition.TaskName}\" /XML");
            if (code != 0 || string.IsNullOrWhiteSpace(xml)) return;

            string? exe = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exe)) return;

            var m = Regex.Match(xml, @"<Command>(.*?)</Command>", RegexOptions.Singleline);
            if (!m.Success) return;

            string registered = System.Net.WebUtility.HtmlDecode(m.Groups[1].Value.Trim());
            if (!string.Equals(registered, exe, StringComparison.OrdinalIgnoreCase))
            {
                SetEnabled(true);
                Log.Info("已同步更新开机自启动任务的可执行文件路径");
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"检查开机自启动任务路径失败：{ex.Message}");
        }
    }

    public static void SetEnabled(bool enabled)
    {
        if (!enabled)
        {
            var (delCode, delOut) = RunSchtasks($"/Delete /TN \"{StartupTaskDefinition.TaskName}\" /F");
            if (delCode != 0 && IsEnabled())
                throw new InvalidOperationException($"删除开机自启动任务失败：{delOut}");
            return;
        }

        string? exePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
            throw new InvalidOperationException("无法确定当前可执行文件路径。");

        string xmlPath = Path.Combine(Path.GetTempPath(), $"snugwin-autostart-{Guid.NewGuid():N}.xml");
        try
        {
            string xml = StartupTaskDefinition.BuildTaskXml(exePath, Path.GetDirectoryName(exePath));
            File.WriteAllText(xmlPath, xml, Encoding.Unicode);

            var (code, output) = RunSchtasks($"/Create /TN \"{StartupTaskDefinition.TaskName}\" /XML \"{xmlPath}\" /F");
            if (code != 0)
                throw new InvalidOperationException($"注册开机自启动任务失败（退出码 {code}）：{output}");
        }
        finally
        {
            try { File.Delete(xmlPath); } catch { }
        }
    }

    private static (int ExitCode, string Output) RunSchtasks(string arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "schtasks.exe",
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("无法启动 schtasks.exe");
        string stdout = proc.StandardOutput.ReadToEnd();
        string stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit(8000);
        return (proc.ExitCode, string.IsNullOrWhiteSpace(stderr) ? stdout : stderr);
    }
}
