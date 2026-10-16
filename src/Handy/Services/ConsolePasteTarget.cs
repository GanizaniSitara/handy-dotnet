using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Handy.PInvoke;

namespace Handy.Services;

// Match the console owner and its actual clients. Window titles and model
// names do not identify an input protocol. Windows Terminal handles its own
// clipboard paste framing; this probe is only for classic console windows.
public sealed record ConsolePasteTarget(IntPtr Hwnd, uint OwnerPid, uint ClientPid,
    DateTime ClientStartedUtc, string Executable)
{
    public static bool IsGeminiClient(string executable, string commandLine)
    {
        if (executable.Equals("agy.exe", StringComparison.OrdinalIgnoreCase)
            || executable.Equals("gemini.exe", StringComparison.OrdinalIgnoreCase)) return true;
        if (!executable.Equals("node.exe", StringComparison.OrdinalIgnoreCase)) return false;
        // Examine the script argument, rather than matching prompt text later
        // in argv. Both the package's bundle and dist entry points are used.
        var rest = commandLine.TrimStart();
        rest = SkipArgument(rest);
        var script = FirstArgument(rest).Replace('\\', '/');
        return script.Contains("/@google/gemini-cli/", StringComparison.OrdinalIgnoreCase);
    }

    private static string FirstArgument(string value)
    {
        value = value.TrimStart();
        if (value.StartsWith('"'))
        {
            var end = value.IndexOf('"', 1);
            return end < 0 ? "" : value[1..end];
        }
        var space = value.IndexOfAny(new[] { ' ', '\t' });
        return space < 0 ? value : value[..space];
    }

    private static string SkipArgument(string value)
    {
        if (value.StartsWith('"'))
        {
            var end = value.IndexOf('"', 1);
            return end < 0 ? "" : value[(end + 1)..].TrimStart();
        }
        var space = value.IndexOfAny(new[] { ' ', '\t' });
        return space < 0 ? "" : value[space..].TrimStart();
    }

    public static ConsolePasteTarget? Probe(IntPtr hwnd)
    {
        var cls = new StringBuilder(128);
        NativeMethods.GetClassName(hwnd, cls, cls.Capacity);
        if (cls.ToString() != "ConsoleWindowClass") return null;
        NativeMethods.GetWindowThreadProcessId(hwnd, out var owner);
        if (owner == 0) return null;
        var snapshot = CreateToolhelp32Snapshot(2, 0);
        if (snapshot == new IntPtr(-1)) return null;
        var entries = new List<ProcessEntry>();
        try
        {
            var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
            if (!Process32FirstW(snapshot, ref entry)) return null;
            do { entries.Add(entry); } while (Process32NextW(snapshot, ref entry));
        }
        finally { NativeMethods.CloseHandle(snapshot); }

        ConsolePasteTarget? target = null;
        foreach (var entry in entries)
        {
            // Normal cmd / PowerShell launchers host the CLI as a direct child.
            // Do not follow arbitrary descendants into separately opened windows.
            if (entry.Pid != owner && entry.ParentPid != owner) continue;
            if (!IsGeminiClient(entry.Exe, entry.Exe.Equals("node.exe", StringComparison.OrdinalIgnoreCase)
                ? ReadCommandLine(entry.Pid) : "")) continue;
            if (target is not null) return null;
            try
            {
                using var process = Process.GetProcessById((int)entry.Pid);
                target = new(hwnd, owner, entry.Pid, process.StartTime.ToUniversalTime(), entry.Exe);
            }
            catch { return null; }
        }
        return target;
    }

    public bool IsCurrent(IntPtr hwnd)
    {
        if (hwnd != Hwnd) return false;
        NativeMethods.GetWindowThreadProcessId(hwnd, out var owner);
        if (owner != OwnerPid) return false;
        try
        {
            using var process = Process.GetProcessById((int)ClientPid);
            return !process.HasExited && process.StartTime.ToUniversalTime() == ClientStartedUtc;
        }
        catch { return false; }
    }

    private static string ReadCommandLine(uint pid)
    {
        var handle = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle == IntPtr.Zero) return "";
        IntPtr buffer = IntPtr.Zero;
        try
        {
            NtQueryInformationProcess(handle, 60, IntPtr.Zero, 0, out var size);
            if (size == 0 || size > 131072) return "";
            buffer = Marshal.AllocHGlobal((int)size);
            if (NtQueryInformationProcess(handle, 60, buffer, size, out _) != 0) return "";
            var value = Marshal.PtrToStructure<UnicodeString>(buffer);
            return Marshal.PtrToStringUni(value.Buffer, value.Length / 2) ?? "";
        }
        finally
        {
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
            NativeMethods.CloseHandle(handle);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString { public ushort Length, MaximumLength; public IntPtr Buffer; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size, Usage, Pid;
        public UIntPtr Heap;
        public uint Module, Threads, ParentPid;
        public int Priority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Exe;
    }
    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr handle, int infoClass, IntPtr buffer, uint size, out uint returned);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32FirstW(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32NextW(IntPtr snapshot, ref ProcessEntry entry);
}
