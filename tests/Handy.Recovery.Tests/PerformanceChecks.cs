using System;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Handy.Services;

static class PerformanceChecks
{
    public static async Task Run()
    {
        var buffer = new DisplayLogBuffer();
        for (int i = 0; i < 10000; i++) buffer.Append($"{i}:" + new string('x', 100));
        var rendered = buffer.TakeChangedText()!;
        Check(rendered.Length <= 65536 && rendered.Contains("9999:") && !rendered.StartsWith("0:"), "bounded newest log tail");
        Check(buffer.TakeChangedText() is null, "unchanged log does not render again");
        buffer.Append(new string('x', 100000));
        Check(buffer.TakeChangedText()!.Length <= 65536, "oversized line bounded");
        using var missing = new SileroVadService(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".onnx"));
        var settings = new AppSettings { BackgroundRecognitionEnabled = true };
        var unknown = missing.Analyze(new float[3200]);
        Check(unknown.Status == SileroVadService.SpeechStatus.Uncertain, "missing VAD cannot prove silence");
        Check(SpeculativeCachePolicy.Decide(19200, 16000, unknown, settings) == SpeculativeCachePolicy.Decision.ColdPass, "unknown tail preserves full decode");
        await ModelLoading();
        await CompletedPhrases();
        PriorityRestored();
        Console.WriteLine("Background model loading, bounded logs and conservative VAD checks passed.");
    }

    private static async Task ModelLoading()
    {
        var app = (Handy.App)RuntimeHelpers.GetUninitializedObject(typeof(Handy.App));
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        void Set(string name, object value) => typeof(Handy.App).GetField(name, flags)!.SetValue(app, value);
        object? Get(string name) => typeof(Handy.App).GetField(name, flags)!.GetValue(app);
        using var gate = new SemaphoreSlim(1, 1);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        Set("_asrGate", gate);
        Set("_settings", new AppSettings { ParakeetEncoderThreads = 8, ParakeetPrioritizeInference = true });
        Set("_dataDir", "");
        Set("_parakeetModelDir", "");
        Set("_modelLoadTask", Task.CompletedTask);
        Set("_createVad", new Func<string, SileroVadService>(_ => new SileroVadService("missing-test-vad.onnx")));
        var discarded = new FakeAsr();
        var published = new FakeAsr();
        int factories = 0;
        Set("_createAsr", new Func<AppSettings, string, string, ITranscriptionService>((selection, _, _) =>
        {
            Check(selection.ParakeetEncoderThreads == 8 && selection.ParakeetPrioritizeInference,
                "background model selection retains machine tuning");
            if (Interlocked.Increment(ref factories) != 1) return published;
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new Exception("load release timeout");
            return discarded;
        }));
        var queue = typeof(Handy.App).GetMethod("QueueModelLoad", flags)!;
        queue.Invoke(app, new object[] { "test-startup" });
        Check(entered.Wait(TimeSpan.FromSeconds(5)), "factory entered on worker");
        Check((bool)Get("_modelLoading")! && Get("_asr") is null, "loading state available while factory blocked");
        queue.Invoke(app, new object[] { "test-new-selection" });
        release.Set();
        await ((Task)Get("_modelLoadTask")!).WaitAsync(TimeSpan.FromSeconds(10));
        Check(discarded.Disposed && !published.Disposed && ReferenceEquals(Get("_asr"), published), "stale selection disposed; latest published");
        Check(!(bool)Get("_modelLoading")!, "loading cleared after publish");
        (Get("_asrWarmup") as IDisposable)?.Dispose();
        published.Dispose();
        (Get("_vad") as IDisposable)?.Dispose();

        // Shutdown arrives during a model construction; it must not publish or
        // launch a warmup with a session that cleanup is about to dispose.
        Set("_modelLoadTask", Task.CompletedTask);
        Set("_shutdownCleanupStarted", 0);
        entered.Reset(); release.Reset();
        var shutdownCandidate = new FakeAsr();
        Set("_createAsr", new Func<AppSettings, string, string, ITranscriptionService>((_, _, _) =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new Exception("shutdown release timeout");
            return shutdownCandidate;
        }));
        queue.Invoke(app, new object[] { "test-shutdown" });
        Check(entered.Wait(TimeSpan.FromSeconds(5)), "shutdown factory entered");
        Set("_shutdownCleanupStarted", 1);
        release.Set();
        await ((Task)Get("_modelLoadTask")!).WaitAsync(TimeSpan.FromSeconds(10));
        Check(shutdownCandidate.Disposed, "shutdown candidate drained and disposed");

        Set("_shutdownCleanupStarted", 0);
        Set("_modelLoadTask", Task.CompletedTask);
        Set("_createAsr", new Func<AppSettings, string, string, ITranscriptionService>((_, _, _) => throw new InvalidOperationException("test loading failure")));
        queue.Invoke(app, new object[] { "test-failure" });
        await ((Task)Get("_modelLoadTask")!).WaitAsync(TimeSpan.FromSeconds(10));
        Check(!(bool)Get("_modelLoading")! && Get("_asr") is null, "loading failure clears readiness without faulting worker");
    }

    private static async Task CompletedPhrases()
    {
        var app = (Handy.App)RuntimeHelpers.GetUninitializedObject(typeof(Handy.App));
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        void Set(string name, object value) => typeof(Handy.App).GetField(name, flags)!.SetValue(app, value);
        using var audio = new AudioCaptureService(); // deliberately no microphone initialization
        using var gate = new SemaphoreSlim(1, 1);
        var pcm = new MemoryStream();
        typeof(AudioCaptureService).GetField("_recBuffer", flags)!.SetValue(audio, pcm);
        typeof(AudioCaptureService).GetField("_recording", flags)!.SetValue(audio, true);
        pcm.Write(new byte[4 * 16000 * 2]);
        Set("_audio", audio);
        Set("_asrGate", gate);
        Set("_recording", true);
        Set("_settings", new AppSettings { BackgroundRecognitionEnabled = true, VadEnabled = false });
        var asr = new PhraseAsr();
        var worker = typeof(Handy.App).GetMethod("RunSpeculativePass", flags)!;
        async Task Pass() => await (Task)worker.Invoke(app, new object[] { asr, CancellationToken.None, false })!;
        await Pass();
        pcm.Write(new byte[3 * 16000 * 2]);
        await Pass();
        Check(asr.Lengths.SequenceEqual(new[] { 64000, 60800 }), "second phrase decodes new 3 seconds plus 800 ms, not 7-second prefix");
        var cache = typeof(Handy.App).GetField("_specCache", flags)!.GetValue(app)!;
        Check((string)cache.GetType().GetProperty("RawText")!.GetValue(cache)! == "one two three", "phrase overlap spliced once");
        // A continuing pause, however long, cannot schedule another speculative
        // pass without a new non-silent callback.
        Set("_lastNonSilentTicks", 1L);
        Set("_lastSpecSpeechTicks", 1L);
        using var cts = new CancellationTokenSource();
        Set("_specCts", cts);
        Set("_asr", asr);
        typeof(Handy.App).GetMethod("OnAudioLevelsForSpeculative", flags)!.Invoke(app, new object[] { new float[5] });
        Check((int)typeof(Handy.App).GetField("_specInFlight", flags)!.GetValue(app)! == 0, "same pause does not dispatch again");
    }

    private static void PriorityRestored()
    {
        using var process = Process.GetCurrentProcess();
        var original = process.PriorityClass;
        var scopeType = typeof(ParakeetTranscriptionService).GetNestedType("InferencePriorityScope", BindingFlags.NonPublic)!;
        try
        {
            process.PriorityClass = ProcessPriorityClass.Normal;
            try
            {
                using var scope = (IDisposable)Activator.CreateInstance(scopeType)!;
                process.Refresh();
                Check(process.PriorityClass == ProcessPriorityClass.AboveNormal, "inference temporarily prioritized");
                throw new OperationCanceledException("exercise exceptional unwind");
            }
            catch (OperationCanceledException) { }
            process.Refresh();
            Check(process.PriorityClass == ProcessPriorityClass.Normal, "priority restored on exceptional unwind");
            process.PriorityClass = ProcessPriorityClass.AboveNormal;
            using ((IDisposable)Activator.CreateInstance(scopeType)!) { }
            process.Refresh();
            Check(process.PriorityClass == ProcessPriorityClass.AboveNormal, "existing priority preserved");
        }
        finally { process.PriorityClass = original; }
    }

    public static async Task BenchEncoder(string modelDir, string wav)
    {
        EnableBenchmarkLog();
        using var four = new ParakeetTranscriptionService(modelDir, encoderThreads: 4);
        using var eight = new ParakeetTranscriptionService(modelDir, encoderThreads: 8);
        Check(four.IsReady && eight.IsReady, "paired encoder models ready");
        var samples = ReadWav(wav);
        using var process = Process.GetCurrentProcess();
        foreach (int seconds in new[] { 3, 7, 11 })
        {
            var clip = samples.Take(seconds * 16000).ToArray();
            var expected = await four.TranscribeAsync(clip);
            Check(await eight.TranscribeAsync(clip) == expected, "encoder warm parity");
            for (int pass = 0; pass < 8; pass++)
            {
                var more = pass % 4 is 1 or 2;
                var cpu = process.TotalProcessorTime;
                var timer = Stopwatch.StartNew();
                var text = await (more ? eight : four).TranscribeAsync(clip);
                var wallMs = timer.Elapsed.TotalMilliseconds;
                Check(text == expected, "paired encoder transcript parity");
                Console.WriteLine(JsonSerializer.Serialize(new { seconds, pass, threads = more ? 8 : 4,
                    wallMs, cpuMs = (process.TotalProcessorTime - cpu).TotalMilliseconds, text }));
            }
        }
    }

    public static async Task BenchProvider(string modelDir, string wav, string provider, string? cudaLibraryDir, int passes = 4, int cudaDeviceId = 0)
    {
        EnableBenchmarkLog();
        var loadTimer = Stopwatch.StartNew();
        using var asr = new ParakeetTranscriptionService(modelDir, encoderThreads: 8, prioritizeInference: true,
            executionProvider: provider, cudaLibraryDir: cudaLibraryDir, cudaDeviceId: cudaDeviceId);
        Check(asr.IsReady, "provider benchmark model ready");
        Console.WriteLine(JsonSerializer.Serialize(new { requested = provider, actual = asr.ExecutionProvider, loadMs = loadTimer.ElapsedMilliseconds }));
        var samples = ReadWav(wav);
        foreach (int seconds in new[] { 3, 7, 11, 20 })
        {
            var clip = seconds * 16000 <= samples.Length
                ? samples.Take(seconds * 16000).ToArray()
                : samples.Concat(samples).Take(seconds * 16000).ToArray();
            for (int pass = 0; pass < passes; pass++)
            {
                var timer = Stopwatch.StartNew();
                var text = await asr.TranscribeAsync(clip);
                Console.WriteLine(JsonSerializer.Serialize(new { seconds, pass, provider = asr.ExecutionProvider,
                    wallMs = Math.Round(timer.Elapsed.TotalMilliseconds), text }));
            }
        }
    }

    public static async Task BenchPriority(string modelDir, string wav, int encoderThreads)
    {
        EnableBenchmarkLog();
        using var asr = new ParakeetTranscriptionService(modelDir, encoderThreads: encoderThreads);
        Check(asr.IsReady, "priority benchmark model ready");
        var samples = ReadWav(wav);
        using var process = Process.GetCurrentProcess();
        var original = process.PriorityClass;
        try
        {
            foreach (int seconds in new[] { 3, 11 })
            {
                var clip = samples.Take(seconds * 16000).ToArray();
                var expected = await asr.TranscribeAsync(clip);
                for (int pass = 0; pass < 8; pass++)
                {
                    var boost = pass % 4 is 1 or 2; // ABBA, twice, same loaded model.
                    process.PriorityClass = boost ? ProcessPriorityClass.AboveNormal : ProcessPriorityClass.Normal;
                    var cpu = process.TotalProcessorTime;
                    var timer = Stopwatch.StartNew();
                    var text = await asr.TranscribeAsync(clip);
                    var wallMs = timer.Elapsed.TotalMilliseconds;
                    Check(text == expected, "priority transcript parity");
                    Console.WriteLine(JsonSerializer.Serialize(new { seconds, pass, boost, wallMs,
                        cpuMs = (process.TotalProcessorTime - cpu).TotalMilliseconds, text }));
                }
            }
        }
        finally { process.PriorityClass = original; }
    }

    public static async Task BenchThreads(string modelDir, string wav, string mode)
    {
        EnableBenchmarkLog();
        var samples = ReadWav(wav);
        var references = new System.Collections.Generic.Dictionary<int, string>();
        // Return to baseline between candidates to expose changing host load.
        var profiles = mode switch
        {
            "baseline" => new[] { (4, 4, true) },
            "tuned" => new[] { (8, 4, true) },
            "extended" => new[] { (4, 4, true), (4, 1, true), (8, 4, true), (4, 4, false), (4, 4, true) },
            "contention" => new[] { (4, 4, true), (4, 1, false), (4, 4, true), (2, 1, false), (1, 1, false), (4, 4, true) },
            _ => throw new ArgumentException("Unknown thread benchmark mode", nameof(mode)),
        };
        foreach (var profile in profiles)
        {
            using var asr = new ParakeetTranscriptionService(modelDir, encoderThreads: profile.Item1,
                auxiliaryThreads: profile.Item2, allowSpinning: profile.Item3, prioritizeInference: mode == "tuned");
            Check(asr.IsReady, "thread benchmark model ready");
            using var process = Process.GetCurrentProcess();
            foreach (int seconds in new[] { 3, 7, 11 })
            {
                var clip = samples.Take(seconds * 16000).ToArray();
                for (int pass = 0; pass < 3; pass++)
                {
                    var cpu = process.TotalProcessorTime;
                    var allocated = GC.GetTotalAllocatedBytes();
                    var timer = Stopwatch.StartNew();
                    var text = await asr.TranscribeAsync(clip);
                    var wallMs = timer.Elapsed.TotalMilliseconds;
                    process.Refresh();
                    Check(process.PriorityClass == ProcessPriorityClass.Normal, "benchmark decode restores normal priority");
                    var cpuMs = (process.TotalProcessorTime - cpu).TotalMilliseconds;
                    if (!references.TryGetValue(seconds, out var expected)) references[seconds] = expected = text;
                    Check(text == expected, "thread profile transcript parity");
                    Console.WriteLine(JsonSerializer.Serialize(new { encoderThreads = profile.Item1,
                        auxiliaryThreads = profile.Item2, spin = profile.Item3, seconds, pass, wallMs, cpuMs,
                        allocatedBytes = GC.GetTotalAllocatedBytes() - allocated, text }));
                }
            }
        }
    }

    public static async Task BenchCache(string modelDir, string wav, string cacheRoot, string mode)
    {
        EnableBenchmarkLog();
        Check(mode is "original" or "save" or "cached", "cache benchmark mode");
        var hashTimer = Stopwatch.StartNew();
        var identity = new StringBuilder("CPU/inter1/intra4/arenaFalse/ALL|")
            .Append(typeof(Microsoft.ML.OnnxRuntime.InferenceSession).Assembly.GetName().Version)
            .Append('|').Append(Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER"));
        foreach (var name in new[] { "nemo128.onnx", "encoder-model.int8.onnx", "decoder_joint-model.int8.onnx", "vocab.txt" })
        {
            using var input = File.OpenRead(Path.Combine(modelDir, name));
            identity.Append('|').Append(name).Append(':').Append(Convert.ToHexString(SHA256.HashData(input)));
        }
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity.ToString())));
        var hashMs = hashTimer.ElapsedMilliseconds;
        var cache = Path.Combine(cacheRoot, key);
        var timer = Stopwatch.StartNew();
        using var asr = new ParakeetTranscriptionService(modelDir, mode == "original" ? null : cache, mode == "cached");
        Check(asr.IsReady, "cache benchmark model ready");
        var loadMs = timer.ElapsedMilliseconds;
        var samples = ReadWav(wav);
        var results = new System.Collections.Generic.List<object>();
        foreach (int seconds in new[] { 3, 7, 11 })
        {
            var clip = samples.Take(seconds * 16000).ToArray();
            for (int i = 0; i < 3; i++)
            {
                timer.Restart();
                var text = await asr.TranscribeAsync(clip);
                results.Add(new { seconds, pass = i + 1, asrMs = timer.ElapsedMilliseconds, text });
            }
        }
        Console.WriteLine(JsonSerializer.Serialize(new { mode, key, hashMs, loadMs, results }));
    }

    public static async Task BenchVad(string modelDir, string vadPath, string wav)
    {
        EnableBenchmarkLog();
        using var vad = new SileroVadService(vadPath);
        Check(vad.IsReady, "installed VAD ready");
        var settings = new AppSettings { BackgroundRecognitionEnabled = true };
        foreach (int ms in new[] { 50, 100, 200, 1000, 3000 })
        {
            var audio = new float[ms * 16];
            var analysis = vad.Analyze(audio, 0.3, 500);
            var decision = SpeculativeCachePolicy.Decide(16000 + audio.Length, 16000, analysis, settings);
            Check(decision == SpeculativeCachePolicy.Decision.UsePrefixOnly, "actual silence policy " + ms);
            Console.WriteLine($"silenceTailMs={ms} status={analysis.Status} preservedSamples={analysis.Samples.Length} decision={decision}");
        }
        var samples = ReadWav(wav);
        var quiet = samples.Select(v => v * 0.005f).ToArray();
        var quietAnalysis = vad.Analyze(quiet, 0.3, 500);
        Check(quietAnalysis.Status != SileroVadService.SpeechStatus.Silence && quietAnalysis.Samples.Length > 0, "quiet speech cannot become empty tail");
        Console.WriteLine($"quietSpeech status={quietAnalysis.Status} maxProb={quietAnalysis.MaxProbability:F3} preservedSamples={quietAnalysis.Samples.Length}");
        using var asr = new ParakeetTranscriptionService(modelDir);
        Check(asr.IsReady, "post-roll benchmark ASR ready");
        var quietTail = (float[])samples.Clone();
        for (int i = Math.Max(0, quietTail.Length - 2 * 16000); i < quietTail.Length; i++) quietTail[i] *= 0.005f;
        var ordinaryText = await asr.TranscribeAsync(samples);
        var quietTailText = await asr.TranscribeAsync(vad.Trim(quietTail, 0.3, 500));
        Console.WriteLine(JsonSerializer.Serialize(new { quietFinalTwoSeconds = true, gain = 0.005,
            sameWords = Words(ordinaryText) == Words(quietTailText), ordinaryText, quietTailText }));
        // Controlled early release: locate the last audible sample and model
        // stopping 100 ms before it. This intentionally challenges final words.
        var last = Array.FindLastIndex(samples, v => Math.Abs(v) >= 0.002f) + 1;
        var release = Math.Max(0, last - 1600);
        string? baseline = null;
        foreach (int ms in new[] { 200, 100, 50 })
        {
            var clip = samples.Take(Math.Min(samples.Length, release + ms * 16)).ToArray();
            var text = await asr.TranscribeAsync(vad.Trim(clip, 0.3, 500));
            baseline ??= text;
            Console.WriteLine(JsonSerializer.Serialize(new { postRollMs = ms, releaseSample = release, capturedSamples = clip.Length, matches200 = text == baseline, text }));
        }
        // Compare completed-phrase processing against repeatedly decoding a
        // growing prefix, with the same natural pause boundaries and lookback.
        var finder = typeof(Handy.App).GetMethod("FindSilenceAlignedSplit", BindingFlags.Static | BindingFlags.NonPublic)!;
        var boundaries = new[] { 3 * 16000, 7 * 16000 }
            .Select(target => (int)finder.Invoke(null, new object[] { samples, target, 250, 1500 })!)
            .Where(cut => cut > 0 && cut < samples.Length).Distinct().OrderBy(cut => cut).ToList();
        Check(boundaries.Count > 0, "natural pause available for phrase benchmark");
        boundaries.Add(samples.Length);
        int previous = 0;
        string cumulative = string.Empty;
        long growingMs = 0, phraseMs = 0;
        foreach (int boundary in boundaries)
        {
            var timer = Stopwatch.StartNew();
            var growing = await asr.TranscribeAsync(vad.Trim(samples.Take(boundary).ToArray(), 0.3, 500));
            var fullMs = timer.ElapsedMilliseconds;
            growingMs += fullMs;
            int start = Math.Max(0, previous - 12800);
            timer.Restart();
            var phrase = await asr.TranscribeAsync(vad.Trim(samples.Skip(start).Take(boundary - start).ToArray(), 0.3, 500));
            var segmentMs = timer.ElapsedMilliseconds;
            phraseMs += segmentMs;
            cumulative = TranscriptSplicer.Combine(cumulative, phrase);
            Console.WriteLine(JsonSerializer.Serialize(new { boundary, growingSamples = boundary, phraseSamples = boundary - start,
                growingMs = fullMs, phraseMs = segmentMs, growing, cumulative,
                sameWords = Words(growing) == Words(cumulative) }));
            previous = boundary;
        }
        Console.WriteLine(JsonSerializer.Serialize(new { growingTotalMs = growingMs, phraseTotalMs = phraseMs, boundaries }));
    }

    private static string Words(string text) => string.Join(" ",
        System.Text.RegularExpressions.Regex.Matches(text.ToLowerInvariant(), @"[\p{L}\p{N}']+").Select(m => m.Value));

    private static float[] ReadWav(string path) => (float[])typeof(Handy.App).Assembly.GetType("Handy.Services.WavIo")!
        .GetMethod("ReadMonoFloat16k")!.Invoke(null, new object[] { path })!;
    private static void EnableBenchmarkLog()
    {
        var logger = typeof(Handy.App).Assembly.GetType("Handy.Log")!;
        var verbosity = typeof(Handy.App).Assembly.GetType("Handy.LogVerbosity")!;
        logger.GetMethod("SetVerbosity")!.Invoke(null, new[] { Enum.ToObject(verbosity, 3), Enum.ToObject(verbosity, 3) });
        logger.GetField("Sink", BindingFlags.Static | BindingFlags.Public)!.SetValue(null, new Action<string>(Console.WriteLine));
    }
    private static void Check(bool ok, string what) { if (!ok) throw new Exception("Performance: " + what); }
    private sealed class FakeAsr : ITranscriptionService
    {
        public bool Disposed;
        public bool IsReady => !Disposed;
        public Task<string> TranscribeAsync(float[] samples, TranscriptionOptions? options = null) => Task.FromResult("test");
        public void Dispose() => Disposed = true;
    }
    private sealed class PhraseAsr : ITranscriptionService
    {
        public readonly System.Collections.Generic.List<int> Lengths = new();
        public bool IsReady => true;
        public Task<string> TranscribeAsync(float[] samples, TranscriptionOptions? options = null)
        {
            Lengths.Add(samples.Length);
            return Task.FromResult(Lengths.Count == 1 ? "one two" : "two three");
        }
        public void Dispose() { }
    }
}
