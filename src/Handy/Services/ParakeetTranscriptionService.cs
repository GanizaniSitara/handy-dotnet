using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Handy.Services;

/// <summary>
/// NeMo Parakeet TDT inference via ONNX Runtime. Mirrors the pipeline in
/// transcribe-rs/src/onnx/parakeet/mod.rs:
///
///   1. preprocessor (nemo128.onnx):  waveforms         → features [B,128,T]
///   2. encoder      (encoder*.onnx): features          → encoded  [B,T/8,D]
///   3. decoder+join (decoder_joint): encoded frame + LSTM state → vocab logits
///
/// Decoding is greedy RNN-T (max_symbols_per_step=10). The TDT duration head is
/// deliberately NOT consumed because upstream transcribe-rs ignores it too; using
/// it here makes the port skip encoder frames differently from original Handy.
/// </summary>
public sealed class ParakeetTranscriptionService : ITranscriptionService
{
    private const int MaxSymbolsPerStep = 10;
    private const int SampleRate = 16000;
    private const int PreRollMs = 250;
    private static readonly string[] DecoderOutputNames = { "outputs", "output_states_1", "output_states_2" };

    // Parakeet TDT also emits a duration head after the vocab logits. Upstream
    // transcribe-rs slices logits to vocab_size and advances one encoder frame
    // on blank/max-symbols, so we do the same for parity with original Handy.

    private readonly InferenceSession? _preproc;
    private readonly InferenceSession? _encoder;
    private readonly InferenceSession? _decoder;
    private readonly ParakeetTokenizer? _tok;
    private readonly bool _prioritizeInference;

    // Decoder LSTM state shape, read from ONNX metadata at load time.
    private readonly int _stateLayers;
    private readonly int _stateHidden;

    private int _disposed;
    public bool IsReady => Volatile.Read(ref _disposed) == 0 && _preproc is not null && _encoder is not null && _decoder is not null && _tok is not null;

    /// <summary>Where the encoder actually runs: "CPU" or "CUDA". Only the
    /// encoder moves to the GPU; the preprocessor and the per-token decoder
    /// steps are tiny and would lose more to launch and copy overhead than
    /// they gain.</summary>
    public string ExecutionProvider { get; private set; } = "CPU";

    /// <summary>A model directory holds either the int8 pair (upstream Handy's
    /// download) or the fp32 pair (encoder-model.onnx + .onnx.data). int8 wins
    /// when both are present; fp32 is what a GPU wants.</summary>
    public static bool HasInt8Models(string dir) =>
        File.Exists(Path.Combine(dir, "encoder-model.int8.onnx")) && File.Exists(Path.Combine(dir, "decoder_joint-model.int8.onnx"));
    public static bool HasFp32Models(string dir) =>
        File.Exists(Path.Combine(dir, "encoder-model.onnx")) && File.Exists(Path.Combine(dir, "decoder_joint-model.onnx"));

    // Optimized graphs are opt-in for the offline benchmark, never selected by
    // the tray app until parity and cache compatibility have been established.
    public ParakeetTranscriptionService(string modelDir, string? optimizedModelDir = null, bool loadOptimized = false,
        int encoderThreads = 4, int auxiliaryThreads = 4, bool allowSpinning = true, bool prioritizeInference = false,
        string executionProvider = "CPU", int cudaDeviceId = 0, string? cudaLibraryDir = null)
    {
        _prioritizeInference = prioritizeInference;
        try
        {
            var wantCuda = string.Equals(executionProvider, "CUDA", StringComparison.OrdinalIgnoreCase);
            var preprocPath = Path.Combine(modelDir, "nemo128.onnx");
            // Encoder: a GPU takes fp32 when it is there; a CPU takes int8 when
            // it is there. Decoder: always int8 when present. Its per-step cost
            // is bound by re-reading its weights, and fp32 weights are four
            // times the bytes: measured 11 ms per step against under 1 ms.
            var int8Encoder = File.Exists(Path.Combine(modelDir, "encoder-model.int8.onnx"));
            var fp32Encoder = File.Exists(Path.Combine(modelDir, "encoder-model.onnx"));
            var int8 = int8Encoder && !(wantCuda && fp32Encoder);
            var encoderPath = Path.Combine(modelDir, int8 ? "encoder-model.int8.onnx" : "encoder-model.onnx");
            var decoderPath = Path.Combine(modelDir, "decoder_joint-model.int8.onnx");
            if (!File.Exists(decoderPath)) decoderPath = Path.Combine(modelDir, "decoder_joint-model.onnx");
            var vocabPath   = Path.Combine(modelDir, "vocab.txt");

            foreach (var p in new[] { preprocPath, encoderPath, decoderPath, vocabPath })
            {
                if (!File.Exists(p))
                {
                    Log.Warn($"Parakeet asset missing: {p}");
                    return;
                }
            }

            using var opts = new SessionOptions
            {
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                InterOpNumThreads = 1,
                // Pinned rather than 0 ("let ORT pick"): an unbounded IntraOp
                // count multiplies the per-thread CPU arena below across every
                // thread ORT spins up.
                IntraOpNumThreads = auxiliaryThreads,
                // The CPU arena grows to the largest input it has ever seen and
                // never releases it back to the OS. Each speculative pass feeds
                // a longer prefix than the last, so over a long session the
                // arena only ratchets upward. Disabling it costs a bit of
                // per-call allocation overhead in exchange for not holding a
                // session's high-water mark for its entire lifetime.
                EnableCpuMemArena = false,
            };
            opts.AddSessionConfigEntry("session.intra_op.allow_spinning", allowSpinning ? "1" : "0");
            opts.AddSessionConfigEntry("session.inter_op.allow_spinning", allowSpinning ? "1" : "0");
            Log.Info($"Session: Parakeet runtime encoderThreads={encoderThreads} auxiliaryThreads={auxiliaryThreads} spinning={allowSpinning} prioritizeInference={prioritizeInference}");

            var sw = Stopwatch.StartNew();
            InferenceSession Load(string path)
            {
                opts.IntraOpNumThreads = path == encoderPath ? encoderThreads : auxiliaryThreads;
                if (optimizedModelDir is null) return new InferenceSession(path, opts);
                Directory.CreateDirectory(optimizedModelDir);
                var optimizedPath = Path.Combine(optimizedModelDir, Path.GetFileName(path));
                opts.GraphOptimizationLevel = loadOptimized ? GraphOptimizationLevel.ORT_DISABLE_ALL : GraphOptimizationLevel.ORT_ENABLE_ALL;
                opts.OptimizedModelFilePath = loadOptimized ? string.Empty : optimizedPath;
                return new InferenceSession(loadOptimized ? optimizedPath : path, opts);
            }
            _preproc = Load(preprocPath);
            Log.Info($"Loaded preprocessor in {sw.ElapsedMilliseconds} ms");
            sw.Restart();
            _encoder = string.Equals(executionProvider, "CUDA", StringComparison.OrdinalIgnoreCase)
                ? TryLoadCudaEncoder(encoderPath, cudaDeviceId, cudaLibraryDir) ?? Load(encoderPath)
                : Load(encoderPath);
            Log.Info($"Loaded encoder ({ExecutionProvider}, {(int8 ? "int8" : "fp32")}) in {sw.ElapsedMilliseconds} ms");
            sw.Restart();
            _decoder = Load(decoderPath);
            Log.Info($"Loaded decoder/joiner in {sw.ElapsedMilliseconds} ms");

            _tok = new ParakeetTokenizer(vocabPath);
            Log.Info($"Vocab size={_tok.VocabSize}, blank={_tok.BlankId}");

            Log.Info("Decoder inputs:  " + string.Join(", ",
                _decoder.InputMetadata.Select(kv => $"{kv.Key}[{string.Join(",", kv.Value.Dimensions)}]")));
            Log.Info("Decoder outputs: " + string.Join(", ",
                _decoder.OutputMetadata.Select(kv => $"{kv.Key}[{string.Join(",", kv.Value.Dimensions)}]")));

            // LSTM state shape: [L, 1, H]. Batch is always 1.
            var stateMeta = _decoder.InputMetadata["input_states_1"];
            var dims = stateMeta.Dimensions; // may contain -1 for dynamic
            _stateLayers = dims[0] > 0 ? dims[0] : 2;
            _stateHidden = dims[2] > 0 ? dims[2] : 640;
            Log.Info($"Decoder LSTM state: layers={_stateLayers}, hidden={_stateHidden}");
        }
        catch (Exception ex)
        {
            Dispose();
            Log.Error($"Failed to load Parakeet models: {ex}");
        }
    }

    // The CUDA provider DLL is shipped with the package, but it in turn needs
    // cudart 12, cuBLAS 12 and cuDNN 9, which are not. A machine that has them
    // somewhere (a PyTorch install, the CUDA toolkit) names that directory in
    // settings and it is put on the process PATH before the provider loads.
    // Any failure here is logged and the encoder falls back to the CPU.
    private InferenceSession? TryLoadCudaEncoder(string encoderPath, int deviceId, string? libraryDir)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(libraryDir) && Directory.Exists(libraryDir))
            {
                var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
                if (!path.Split(';').Any(p => string.Equals(p.TrimEnd('\\'), libraryDir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)))
                    Environment.SetEnvironmentVariable("PATH", libraryDir + ";" + path);
            }
            using var cuda = new OrtCUDAProviderOptions();
            cuda.UpdateOptions(new Dictionary<string, string>
            {
                ["device_id"] = deviceId.ToString(),
                // Dictation audio is a different length every time. The default
                // EXHAUSTIVE search re-benchmarks cuDNN's convolution kernels
                // for every new input shape, which is exactly the latency we
                // are trying to remove. HEURISTIC picks once, cheaply.
                ["cudnn_conv_algo_search"] = "HEURISTIC",
                ["arena_extend_strategy"] = "kSameAsRequested",
            });
            using var opts = new SessionOptions
            {
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                InterOpNumThreads = 1,
                IntraOpNumThreads = 2,
                EnableCpuMemArena = false,
            };
            opts.AppendExecutionProvider_CUDA(cuda);
            // HANDY_ORT_VERBOSE=1 prints every node's provider assignment at
            // load, which is how to find an op that silently fell back to CPU.
            if (Environment.GetEnvironmentVariable("HANDY_ORT_VERBOSE") == "1")
                opts.LogSeverityLevel = 0;
            var session = new InferenceSession(encoderPath, opts);
            ExecutionProvider = "CUDA";
            Log.Info($"Session: Parakeet encoder on CUDA device {deviceId}");
            return session;
        }
        catch (Exception ex)
        {
            Log.Warn($"CUDA encoder unavailable, using CPU: {ex.Message.Split('\n')[0]}");
            return null;
        }
    }

    public Task<string> TranscribeAsync(float[] samples, TranscriptionOptions? options = null)
    {
        var queued = Stopwatch.GetTimestamp();
        return Task.Run(() =>
        {
            Log.Info($"Perf: stage=asr-dispatch queueMs={Stopwatch.GetElapsedTime(queued).TotalMilliseconds:F1}");
            return Transcribe(samples);
        });
    }

    private string Transcribe(float[] samples)
    {
        if (!IsReady) throw new InvalidOperationException("Parakeet models not loaded.");
        using var priority = _prioritizeInference ? new InferencePriorityScope() : null;

        // 250 ms of leading silence, matching transcribe-rs::audio::prepend_silence.
        var preRoll = SampleRate * PreRollMs / 1000;
        var padded = new float[preRoll + samples.Length];
        samples.CopyTo(padded, preRoll);

        var total = Stopwatch.StartNew();
        var preProbe = new PerformanceProbe();

        // 1) Preprocessor.
        var waveTensor = new DenseTensor<float>(padded, new[] { 1, padded.Length });
        var waveLens   = new DenseTensor<long>(new[] { (long)padded.Length }, new[] { 1 });

        var preInputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("waveforms",      waveTensor),
            NamedOnnxValue.CreateFromTensor("waveforms_lens", waveLens),
        };

        float[] features;
        int nMels, nFrames;
        long featLen;
        using (var preOut = _preproc!.Run(preInputs))
        {
            var featTensor = preOut[0].AsTensor<float>();
            var lenTensor  = preOut[1].AsTensor<long>();
            var d = featTensor.Dimensions;      // [1, 128, T]
            nMels = d[1];
            nFrames = d[2];
            features = featTensor.ToArray();
            featLen = lenTensor.ToArray()[0];
        }
        Log.Info($"Preproc: {padded.Length} samples → features[1,{nMels},{nFrames}] in {total.ElapsedMilliseconds} ms");

        preProbe.Finish("preprocessor");
        var encoderProbe = new PerformanceProbe();
        // 2) Encoder.
        var encStart = total.ElapsedMilliseconds;
        var encInputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("audio_signal",
                new DenseTensor<float>(features, new[] { 1, nMels, nFrames })),
            NamedOnnxValue.CreateFromTensor("length",
                new DenseTensor<long>(new[] { featLen }, new[] { 1 })),
        };

        float[] encoded;
        int tEnc, dEnc;
        long encLen;
        using (var encOut = _encoder!.Run(encInputs))
        {
            var outTensor = encOut[0].AsTensor<float>();
            var lenTensor = encOut[1].AsTensor<long>();
            var d = outTensor.Dimensions;       // [1, D_enc, T_enc]
            dEnc = d[1];
            tEnc = d[2];
            encoded = outTensor.ToArray();
            encLen = lenTensor.ToArray()[0];
        }
        Log.Info($"Encoder: out[1,{dEnc},{tEnc}], encLen={encLen} in {total.ElapsedMilliseconds - encStart} ms");

        encoderProbe.Finish("encoder");
        var decoderProbe = new PerformanceProbe();
        // 3) Greedy RNN-T decode over vocab logits only, matching transcribe-rs.
        var decStart = total.ElapsedMilliseconds;
        var tokens = new List<int>(capacity: 256);

        var state1 = new float[_stateLayers * 1 * _stateHidden];
        var state2 = new float[_stateLayers * 1 * _stateHidden];
        int lastToken = _tok!.BlankId;
        var frameSlice = new float[dEnc];
        var targetBuffer = new int[1];
        // These buffers belong to this transcription. Run is synchronous, so
        // the next step can reuse them after its output has been consumed.
        var decIn = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("encoder_outputs", new DenseTensor<float>(frameSlice, new[] { 1, dEnc, 1 })),
            NamedOnnxValue.CreateFromTensor("targets", new DenseTensor<int>(targetBuffer, new[] { 1, 1 })),
            NamedOnnxValue.CreateFromTensor("target_length", new DenseTensor<int>(new[] { 1 }, new[] { 1 })),
            NamedOnnxValue.CreateFromTensor("input_states_1", new DenseTensor<float>(state1, new[] { _stateLayers, 1, _stateHidden })),
            NamedOnnxValue.CreateFromTensor("input_states_2", new DenseTensor<float>(state2, new[] { _stateLayers, 1, _stateHidden })),
        };

        int t = 0;
        while (t < encLen)
        {
            // Slice encoder frame t: encoded is [1, dEnc, tEnc]. index = d*tEnc + t.
            for (int d = 0; d < dEnc; d++)
                frameSlice[d] = encoded[d * tEnc + t];

            int emittedThisFrame = 0;
            int advance = 1; // fallback: advance by 1 if nothing else fires

            while (true)
            {
                targetBuffer[0] = lastToken;
                int token;
                using (var decOut = _decoder!.Run(decIn, DecoderOutputNames))
                {
                    var flat = decOut[0].AsTensor<float>();

                    int vs = _tok.VocabSize;
                    int totalLogits = checked((int)flat.Length);
                    int vocabLogits = Math.Min(vs, totalLogits);

                    // Argmax over vocab head.
                    token = _tok.BlankId;
                    float bestTok = float.NegativeInfinity;
                    for (int i = 0; i < vocabLogits; i++)
                    {
                        var value = flat.GetValue(i);
                        if (value > bestTok) { bestTok = value; token = i; }
                    }

                    // Blank must retain the previous LSTM state. It also needs
                    // no state copies, which is most decoder steps.
                    if (token != _tok.BlankId)
                    {
                        decOut[1].AsTensor<float>().ToDenseTensor().Buffer.Span.CopyTo(state1);
                        decOut[2].AsTensor<float>().ToDenseTensor().Buffer.Span.CopyTo(state2);
                    }
                }

                if (token == _tok.BlankId)
                {
                    // Blank emission advances one encoder frame. Do not commit
                    // decoder state on blank; this mirrors transcribe-rs.
                    advance = 1;
                    break;
                }

                // Non-blank: commit state, emit token.
                lastToken = token;
                tokens.Add(token);
                emittedThisFrame++;

                // Stay on the same encoder frame and emit more symbols until
                // blank or the per-frame safety cap advances us.
                if (emittedThisFrame >= MaxSymbolsPerStep)
                {
                    advance = 1;
                    break;
                }
            }

            t += advance;
        }

        var text = _tok.Decode(tokens);
        decoderProbe.Finish("decoder");
        Log.Info($"Decode: {tokens.Count} tokens in {total.ElapsedMilliseconds - decStart} ms; total {total.ElapsedMilliseconds} ms");
        return text;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _preproc?.Dispose();
        _encoder?.Dispose();
        _decoder?.Dispose();
    }

    // ORT's native workers inherit the process priority, not the managed
    // caller's Thread.Priority. Limit the boost to the serialized decode.
    private sealed class InferencePriorityScope : IDisposable
    {
        private readonly Process _process = Process.GetCurrentProcess();
        private bool _changed;

        public InferencePriorityScope()
        {
            try
            {
                if (_process.PriorityClass == ProcessPriorityClass.Normal)
                {
                    _process.PriorityClass = ProcessPriorityClass.AboveNormal;
                    _changed = true;
                }
            }
            catch (Exception ex) { Log.Info($"Perf: inference priority unavailable: {ex.Message}"); }
        }

        public void Dispose()
        {
            try
            {
                _process.Refresh();
                if (_changed && _process.PriorityClass == ProcessPriorityClass.AboveNormal)
                    _process.PriorityClass = ProcessPriorityClass.Normal;
            }
            catch (Exception ex) { Log.Warn($"Inference priority restore failed: {ex.Message}"); }
            finally { _process.Dispose(); }
        }
    }
}
