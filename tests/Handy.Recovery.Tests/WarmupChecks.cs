using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Handy.Services;
using Microsoft.Win32;

static class WarmupChecks
{
    public static async Task Run()
    {
        using var gate = new SemaphoreSlim(1, 1);
        using var asr = new FakeAsr();
        bool busy = false;
        using var warmup = new TranscriptionWarmup(asr, gate, () => busy);
        asr.Ready = false;
        await warmup.RequestAsync("missing");
        Check(asr.Calls == 0, "missing model skipped");
        asr.Ready = true;
        busy = true;
        await warmup.RequestAsync("busy");
        Check(asr.Calls == 0, "recording skipped");
        busy = false;
        gate.Wait();
        await warmup.RequestAsync("decode-active");
        Check(asr.Calls == 0, "active inference skipped without waiting");
        gate.Release();

        asr.Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var startup = warmup.RequestAsync("startup");
        await asr.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check(ReferenceEquals(startup, warmup.RequestAsync("resume")), "duplicate coalesced");
        Check(!gate.Wait(0), "warmup owns shared inference gate");
        asr.Hold.SetResult();
        await startup;
        Check(asr.Calls == 1, "only one warmup ran");
        asr.Hold = null;
        await warmup.RequestAsync("resume");
        Check(asr.Calls == 2, "resume rewarms resident model");
        asr.Fail = true;
        await warmup.RequestAsync("failure");
        Check(gate.Wait(0), "failure releases inference gate");
        gate.Release();
        asr.Fail = false;

        asr.Entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        asr.Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inflight = warmup.RequestAsync("shutdown-test");
        await asr.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var disposing = new ManualResetEventSlim();
        var stop = Task.Run(() => { disposing.Set(); warmup.Dispose(); });
        Check(disposing.Wait(TimeSpan.FromSeconds(5)), "dispose entered");
        Check(!stop.IsCompleted, "dispose waits for native inference");
        asr.Hold.SetResult();
        await Task.WhenAll(inflight, stop).WaitAsync(TimeSpan.FromSeconds(5));
        int calls = asr.Calls;
        await warmup.RequestAsync("after-stop");
        Check(asr.Calls == calls, "stopped worker rejects later resume");
        await CheckResumeRouting();
        Console.WriteLine("ASR warmup lifecycle/concurrency checks passed.");
    }

    private static async Task CheckResumeRouting()
    {
        // Exercise the actual App handler without starting another WPF app,
        // microphone or global keyboard hook alongside the running tray app.
        var app = (Handy.App)RuntimeHelpers.GetUninitializedObject(typeof(Handy.App));
        using var gate = new SemaphoreSlim(1, 1);
        using var asr = new FakeAsr();
        using var warmup = new TranscriptionWarmup(asr, gate, () => false);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(Handy.App).GetField("_asrWarmup", flags)!.SetValue(app, warmup);
        var handler = typeof(Handy.App).GetMethod("OnPowerModeChanged", flags)!;
        void Send(PowerModes mode) => handler.Invoke(app, new object[] { app, new PowerModeChangedEventArgs(mode) });
        Send(PowerModes.Suspend);
        Send(PowerModes.StatusChange);
        Check(asr.Calls == 0, "non-resume events ignored");
        asr.Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Send(PowerModes.Resume);
        await asr.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var pending = warmup.RequestAsync("coalesced-test");
        asr.Hold.SetResult();
        await pending;
        Check(asr.Calls == 1, "App resume event reaches worker");
        typeof(Handy.App).GetField("_shutdownCleanupStarted", flags)!.SetValue(app, 1);
        Send(PowerModes.Resume);
        Check(asr.Calls == 1, "App ignores resume during shutdown");
    }

    // Explicit opt-in integration benchmark using installed models and a local WAV.
    public static async Task Bench(string modelDir, string wav, string mode)
    {
        var samples = (float[])typeof(Handy.App).Assembly.GetType("Handy.Services.WavIo")!
            .GetMethod("ReadMonoFloat16k")!.Invoke(null, new object[] { wav })!;
        // Short first dictation; use a fresh process per mode so JIT/native
        // initialization in a preceding baseline cannot bias the warm result.
        Array.Resize(ref samples, Math.Min(samples.Length, 3 * 16000));
        Check(mode is "cold" or "warm" or "paged" or "paged-warm", "known benchmark mode");
        using var fresh = new ParakeetTranscriptionService(modelDir);
        Check(fresh.IsReady, "benchmark model loaded");
        using var gate = new SemaphoreSlim(1, 1);
        using var warmup = new TranscriptionWarmup(fresh, gate, () => false);
        if (mode.StartsWith("paged"))
        {
            await fresh.TranscribeAsync(samples);
            // Evict only THIS benchmark process's resident pages. This models
            // first-touch after eviction, not a claim of an actual sleep cycle.
            Check(EmptyWorkingSet(Process.GetCurrentProcess().Handle), "test working-set eviction");
        }
        var sw = Stopwatch.StartNew();
        if (mode.EndsWith("warm")) await warmup.RequestAsync("benchmark-" + mode);
        Console.WriteLine($"mode={mode} warmupMs={sw.ElapsedMilliseconds}");
        string? first = null;
        for (int i = 0; i < 3; i++)
        {
            sw.Restart();
            var text = await fresh.TranscribeAsync(samples);
            Console.WriteLine($"pass={i + 1} asrMs={sw.ElapsedMilliseconds} text={text}");
            first ??= text;
            Check(first == text, "repeated speech transcript parity");
        }
    }

    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyWorkingSet(IntPtr process);

    private static void Check(bool ok, string message)
    {
        if (!ok) throw new Exception("Warmup: " + message);
    }

    private sealed class FakeAsr : ITranscriptionService
    {
        public bool Ready = true;
        public bool Fail;
        public int Calls;
        public TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource? Hold;
        public bool IsReady => Ready;
        public async Task<string> TranscribeAsync(float[] samples, TranscriptionOptions? options = null)
        {
            Check(samples.Length == 16000 && Array.TrueForAll(samples, x => x == 0), "synthetic silence only");
            Calls++;
            Entered.TrySetResult();
            if (Hold is not null) await Hold.Task;
            if (Fail) throw new InvalidOperationException("test failure");
            return "must never reach a paste or history path";
        }
        public void Dispose() { }
    }
}
