using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
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
/// deliberately NOT consumed — transcribe-rs ignores it too; layering that on is
/// a future improvement.
/// </summary>
public sealed class ParakeetTranscriptionService : IDisposable
{
    private const int MaxSymbolsPerStep = 10;
    private const int SampleRate = 16000;
    private const int PreRollMs = 250;

    // Parakeet TDT duration head: the last (1030 - vocab_size) logits are durations.
    // For the 0.6b V2/V3 models that's exactly 5 durations: {0, 1, 2, 3, 4}.
    // d=0: emit token, stay on this frame; d>=1: advance by d frames.

    private readonly InferenceSession? _preproc;
    private readonly InferenceSession? _encoder;
    private readonly InferenceSession? _decoder;
    private readonly ParakeetTokenizer? _tok;

    // Decoder LSTM state shape, read from ONNX metadata at load time.
    private readonly int _stateLayers;
    private readonly int _stateHidden;

    public bool IsReady => _preproc is not null && _encoder is not null && _decoder is not null && _tok is not null;

    public ParakeetTranscriptionService(string modelDir)
    {
        try
        {
            var preprocPath = Path.Combine(modelDir, "nemo128.onnx");
            var encoderPath = Path.Combine(modelDir, "encoder-model.int8.onnx");
            var decoderPath = Path.Combine(modelDir, "decoder_joint-model.int8.onnx");
            var vocabPath   = Path.Combine(modelDir, "vocab.txt");

            foreach (var p in new[] { preprocPath, encoderPath, decoderPath, vocabPath })
            {
                if (!File.Exists(p))
                {
                    Log.Warn($"Parakeet asset missing: {p}");
                    return;
                }
            }

            var opts = new SessionOptions
            {
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                InterOpNumThreads = 1,
                IntraOpNumThreads = 0, // 0 = let ORT pick
            };

            var sw = Stopwatch.StartNew();
            _preproc = new InferenceSession(preprocPath, opts);
            Log.Info($"Loaded preprocessor in {sw.ElapsedMilliseconds} ms");
            sw.Restart();
            _encoder = new InferenceSession(encoderPath, opts);
            Log.Info($"Loaded encoder in {sw.ElapsedMilliseconds} ms");
            sw.Restart();
            _decoder = new InferenceSession(decoderPath, opts);
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
            Log.Error($"Failed to load Parakeet models: {ex}");
        }
    }

    public Task<string> TranscribeAsync(float[] samples)
        => Task.Run(() => Transcribe(samples));

    private string Transcribe(float[] samples)
    {
        if (!IsReady) throw new InvalidOperationException("Parakeet models not loaded.");

        // 250 ms of leading silence, matching transcribe-rs::audio::prepend_silence.
        var preRoll = SampleRate * PreRollMs / 1000;
        var padded = new float[preRoll + samples.Length];
        samples.CopyTo(padded, preRoll);

        var total = Stopwatch.StartNew();

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

        // 3) Greedy TDT decode — uses both the vocab head and the duration head.
        var decStart = total.ElapsedMilliseconds;
        var tokens = new List<int>(capacity: 256);

        var state1 = new float[_stateLayers * 1 * _stateHidden];
        var state2 = new float[_stateLayers * 1 * _stateHidden];
        int lastToken = _tok!.BlankId;
        var frameSlice = new float[dEnc];

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
                var encFrame = new DenseTensor<float>(frameSlice, new[] { 1, dEnc, 1 });
                var targets   = new DenseTensor<int>(new[] { lastToken }, new[] { 1, 1 });
                var targetLen = new DenseTensor<int>(new[] { 1 }, new[] { 1 });
                var inState1  = new DenseTensor<float>(state1, new[] { _stateLayers, 1, _stateHidden });
                var inState2  = new DenseTensor<float>(state2, new[] { _stateLayers, 1, _stateHidden });

                var decIn = new List<NamedOnnxValue>
                {
                    NamedOnnxValue.CreateFromTensor("encoder_outputs", encFrame),
                    NamedOnnxValue.CreateFromTensor("targets",         targets),
                    NamedOnnxValue.CreateFromTensor("target_length",   targetLen),
                    NamedOnnxValue.CreateFromTensor("input_states_1",  inState1),
                    NamedOnnxValue.CreateFromTensor("input_states_2",  inState2),
                };

                int token, duration;
                float[] newState1, newState2;
                using (var decOut = _decoder!.Run(decIn))
                {
                    var byName = decOut.ToDictionary(v => v.Name);
                    var flat = byName["outputs"].AsTensor<float>().ToArray();

                    int vs = _tok.VocabSize;
                    int totalLogits = flat.Length;
                    int numDurations = Math.Max(1, totalLogits - vs);

                    // Argmax over vocab head.
                    token = 0;
                    float bestTok = float.NegativeInfinity;
                    for (int i = 0; i < vs && i < totalLogits; i++)
                    {
                        if (flat[i] > bestTok) { bestTok = flat[i]; token = i; }
                    }

                    // Argmax over duration head.
                    duration = 0;
                    float bestDur = float.NegativeInfinity;
                    for (int i = 0; i < numDurations && vs + i < totalLogits; i++)
                    {
                        var v = flat[vs + i];
                        if (v > bestDur) { bestDur = v; duration = i; }
                    }

                    newState1 = byName["output_states_1"].AsTensor<float>().ToArray();
                    newState2 = byName["output_states_2"].AsTensor<float>().ToArray();
                }

                if (token == _tok.BlankId)
                {
                    // Blank emission. Advance by the predicted duration, but at
                    // least 1 — a blank with d=0 would be an infinite loop.
                    advance = Math.Max(1, duration);
                    break;
                }

                // Non-blank: commit state, emit token.
                state1 = newState1;
                state2 = newState2;
                lastToken = token;
                tokens.Add(token);
                emittedThisFrame++;

                if (duration > 0)
                {
                    advance = duration;
                    break;
                }
                // duration == 0: stay on the same frame and emit more — unless
                // we've hit the per-frame safety cap.
                if (emittedThisFrame >= MaxSymbolsPerStep)
                {
                    advance = 1;
                    break;
                }
            }

            t += advance;
        }

        var text = _tok.Decode(tokens);
        Log.Info($"Decode: {tokens.Count} tokens in {total.ElapsedMilliseconds - decStart} ms; total {total.ElapsedMilliseconds} ms");
        return text;
    }

    public void Dispose()
    {
        _preproc?.Dispose();
        _encoder?.Dispose();
        _decoder?.Dispose();
    }
}
