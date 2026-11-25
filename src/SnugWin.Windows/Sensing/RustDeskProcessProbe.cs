using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace SnugWin.Windows.Sensing;

/// <summary>
/// Locates the RustDesk connection-manager process by reading command lines straight from the PEB.
/// The previous WMI path (WbemScripting.SWbemLocator through dynamic) threw COMException 0x80004005
/// while enumerating the second SWbemObject on this machine, which aborted every scan before any
/// window was inspected. This probe uses only kernel32/ntdll and needs no COM.
/// </summary>
internal static class RustDeskProcessProbe
{
    private const string ProcessName = "RustDesk";
    private const int ProcessQueryLimitedInformation = 0x1000;
    private const int ProcessQueryInformation = 0x0400;
    private const int ProcessVmRead = 0x0010;
    private const int ProcessBasicInformationClass = 0;
    private const int PebProcessParametersOffset = 0x20; // x64 PEB.ProcessParameters
    private const int CommandLineOffset = 0x70;          // x64 RTL_USER_PROCESS_PARAMETERS.CommandLine
    private const int MaximumCommandLineBytes = 0x8000;

    private static readonly Regex CmArgument =
        new(@"(?:^|\s)--cm(?:\s|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        public IntPtr ExitStatus;
        public IntPtr PebBaseAddress;
        public IntPtr AffinityMask;
        public IntPtr BasePriority;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        IntPtr processHandle,
        int informationClass,
        ref ProcessBasicInformation information,
        int informationLength,
        out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(int desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(
        IntPtr processHandle,
        IntPtr baseAddress,
        byte[] buffer,
        IntPtr size,
        out IntPtr bytesRead);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    /// <summary>RustDesk process ids in the given session. Empty when none are running.</summary>
    public static HashSet<int> FindSessionProcessIds(int sessionId)
    {
        var ids = new HashSet<int>();
        Process[] processes;
        try { processes = Process.GetProcessesByName(ProcessName); }
        catch { return ids; }

        try
        {
            foreach (var process in processes)
            {
                int pid;
                int sid;
                try
                {
                    pid = process.Id;
                    sid = process.SessionId;
                }
                catch { continue; }
                if (sid == sessionId) ids.Add(pid);
            }
        }
        finally
        {
            foreach (var process in processes) { try { process.Dispose(); } catch { } }
        }
        return ids;
    }

    /// <summary>
    /// True when a RustDesk process in this session runs with --cm (the connection-manager UI that
    /// RustDesk spawns for an accepted remote session), false when every process is readable and none
    /// carries it, null when the command lines could not be read and the answer stays unknown.
    /// </summary>
    public static bool? HasConnectionManagerProcess(int sessionId)
    {
        Process[] processes;
        try { processes = Process.GetProcessesByName(ProcessName); }
        catch { return null; }
        if (processes.Length == 0) return false;

        bool unknown = false;
        try
        {
            foreach (var process in processes)
            {
                int pid;
                int sid;
                try
                {
                    pid = process.Id;
                    sid = process.SessionId;
                }
                catch { unknown = true; continue; }

                if (sid != sessionId) continue;

                string? commandLine = TryReadCommandLine(pid);
                if (commandLine is null) { unknown = true; continue; }
                if (CmArgument.IsMatch(commandLine)) return true;
            }
        }
        finally
        {
            foreach (var process in processes) { try { process.Dispose(); } catch { } }
        }
        return unknown ? null : false;
    }

    /// <summary>Reads a process command line from PEB.ProcessParameters; null when unreadable.</summary>
    internal static string? TryReadCommandLine(int pid)
    {
        IntPtr handle = OpenProcess(ProcessQueryLimitedInformation | ProcessVmRead, false, pid);
        if (handle == IntPtr.Zero)
            handle = OpenProcess(ProcessQueryInformation | ProcessVmRead, false, pid);
        if (handle == IntPtr.Zero) return null;

        try
        {
            var basic = default(ProcessBasicInformation);
            if (NtQueryInformationProcess(handle, ProcessBasicInformationClass, ref basic,
                    Marshal.SizeOf<ProcessBasicInformation>(), out _) != 0)
                return null;
            if (basic.PebBaseAddress == IntPtr.Zero) return null;

            IntPtr parameters = ReadPointer(handle, basic.PebBaseAddress + PebProcessParametersOffset);
            if (parameters == IntPtr.Zero) return null;

            var header = new byte[16]; // UNICODE_STRING { USHORT Length; USHORT MaximumLength; PWSTR Buffer; }
            if (!ReadProcessMemory(handle, parameters + CommandLineOffset, header, (IntPtr)header.Length, out _))
                return null;

            int length = BitConverter.ToUInt16(header, 0);
            if (length <= 0 || length > MaximumCommandLineBytes) return null;

            IntPtr buffer = (IntPtr)(long)BitConverter.ToUInt64(header, 8);
            if (buffer == IntPtr.Zero) return null;

            var text = new byte[length];
            if (!ReadProcessMemory(handle, buffer, text, (IntPtr)length, out _)) return null;
            return Encoding.Unicode.GetString(text);
        }
        catch
        {
            return null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private static IntPtr ReadPointer(IntPtr handle, IntPtr address)
    {
        var raw = new byte[IntPtr.Size];
        if (!ReadProcessMemory(handle, address, raw, (IntPtr)raw.Length, out _)) return IntPtr.Zero;
        return IntPtr.Size == 8 ? (IntPtr)(long)BitConverter.ToUInt64(raw, 0) : (IntPtr)BitConverter.ToUInt32(raw, 0);
    }
}
