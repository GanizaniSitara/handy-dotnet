using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Handy.Services;

// Process-wide counters give evidence about a stall; they do not identify its
// cause. Page faults include soft faults, and GC counters include other threads.
public sealed class PerformanceProbe
{
    private readonly long _started = Stopwatch.GetTimestamp();
    private readonly Snapshot _before = Capture();

    private sealed record Snapshot(double CpuMs, long WorkingSet, long PrivateBytes,
        uint PageFaults, long Allocated, double GcPauseMs, int Gc0, int Gc1, int Gc2,
        ulong Idle, ulong Kernel, ulong User);

    public void Finish(string stage)
    {
        var after = Capture();
        var total = (after.Kernel - _before.Kernel) + (after.User - _before.User);
        var idle = after.Idle - _before.Idle;
        var load = total > 0 ? 100.0 * (total - Math.Min(total, idle)) / total : 0;
        Log.Info($"Perf: stage={stage} wallMs={Stopwatch.GetElapsedTime(_started).TotalMilliseconds:F1} " +
                 $"cpuMs={after.CpuMs - _before.CpuMs:F1} systemBusyPct={load:F1} " +
                 $"pageFaults={unchecked(after.PageFaults - _before.PageFaults)} " +
                 $"workingSetDelta={after.WorkingSet - _before.WorkingSet} privateBytesDelta={after.PrivateBytes - _before.PrivateBytes} " +
                 $"allocatedBytes={after.Allocated - _before.Allocated} gcPauseMs={after.GcPauseMs - _before.GcPauseMs:F1} " +
                 $"gc0={after.Gc0 - _before.Gc0} gc1={after.Gc1 - _before.Gc1} gc2={after.Gc2 - _before.Gc2}");
    }

    private static Snapshot Capture()
    {
        var memory = new MemoryCounters { Size = (uint)Marshal.SizeOf<MemoryCounters>() };
        var process = new IntPtr(-1); // current-process pseudo handle; no OpenProcess
        GetProcessMemoryInfo(process, ref memory, memory.Size);
        GetProcessTimes(process, out _, out _, out var processKernel, out var processUser);
        GetSystemTimes(out var idle, out var kernel, out var user);
        return new((processKernel + processUser) / 10000.0, (long)memory.WorkingSet.ToUInt64(), (long)memory.PrivateUsage.ToUInt64(),
            memory.PageFaultCount, GC.GetTotalAllocatedBytes(false), GC.GetTotalPauseDuration().TotalMilliseconds,
            GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2), idle, kernel, user);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryCounters
    {
        public uint Size, PageFaultCount;
        public UIntPtr PeakWorkingSet, WorkingSet, QuotaPeakPaged, QuotaPaged,
            QuotaPeakNonPaged, QuotaNonPaged, PagefileUsage, PeakPagefileUsage, PrivateUsage;
    }
    [DllImport("psapi.dll")]
    private static extern bool GetProcessMemoryInfo(IntPtr process, ref MemoryCounters counters, uint size);
    [DllImport("kernel32.dll")]
    private static extern bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);
    [DllImport("kernel32.dll")]
    private static extern bool GetProcessTimes(IntPtr process, out ulong creation, out ulong exit, out ulong kernel, out ulong user);
}
