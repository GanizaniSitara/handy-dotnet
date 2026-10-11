using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Handy.PInvoke;

namespace Handy.Services;

// Read-only process inspection. Never attach to another application's console
// or infer its input protocol from a task title.
internal sealed record ConsolePasteTarget(IntPtr Hwnd, uint OwnerPid, uint ClientPid, DateTime ClientStartedUtc)
{
    internal static ConsolePasteTarget? Probe(IntPtr hwnd)
    {
        var cls = new StringBuilder(128);
        NativeMethods.GetClassName(hwnd, cls, cls.Capacity);
        if (cls.ToString() != "ConsoleWindowClass") return null;
        NativeMethods.GetWindowThreadProcessId(hwnd, out var owner);
        if (owner == 0) return null;

        var snapshot = CreateToolhelp32Snapshot(2, 0);
        if (snapshot == new IntPtr(-1)) return null;
        var processes = new List<ProcessEntry>();
        try
        {
            var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
            if (!Process32FirstW(snapshot, ref entry)) return null;
            do { processes.Add(entry); } while (Process32NextW(snapshot, ref entry));
        }
        finally { NativeMethods.CloseHandle(snapshot); }

        uint client = 0;
        foreach (var entry in processes)
        {
            if ((entry.Pid == owner || entry.ParentPid == owner)
                && string.Equals(entry.Exe, "agy.exe", StringComparison.OrdinalIgnoreCase))
            {
                if (client != 0) return null; // Ambiguous console: keep configured method.
                client = entry.Pid;
            }
        }
        if (client == 0) return null;
        using var process = Process.GetProcessById((int)client);
        return new ConsolePasteTarget(hwnd, owner, client, process.StartTime.ToUniversalTime());
    }

    internal bool IsCurrent(IntPtr hwnd)
    {
        if (hwnd != Hwnd) return false;
        NativeMethods.GetWindowThreadProcessId(hwnd, out var owner);
        if (owner != OwnerPid) return false;
        try
        {
            using var process = Process.GetProcessById((int)ClientPid);
            return !process.HasExited && process.ProcessName.Equals("agy", StringComparison.OrdinalIgnoreCase)
                && process.StartTime.ToUniversalTime() == ClientStartedUtc;
        }
        catch { return false; }
    }

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

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32FirstW(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32NextW(IntPtr snapshot, ref ProcessEntry entry);
}
