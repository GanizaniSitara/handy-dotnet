using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Handy.Services;

/// <summary>
/// Silero VAD v5 (snakers4/silero-vad, master). Takes fixed 512-sample frames
/// of float32 mono 16 kHz audio and returns speech probability in [0, 1]. We
/// use it to trim leading/trailing silence before Parakeet transcription.
///
/// ONNX signature (verified at load):
///   input: "input"  float32 [batch, 512]
///   input: "sr"     int64   scalar (16000)
///   input: "state"  float32 [2, batch, 128]
///   output: "output" float32 [batch, 1]
///   output: "stateN" float32 [2, batch, 128]
/// </summary>
public sealed class SileroVadService : IDisposable
{
    private const int SampleRate = 16000;
    private const int FrameSize  = 512;
    private const int ContextSize = 64;   // 16 kHz context window prepended to each frame (Silero v5)
    private const int StateHidden = 128;

    private readonly InferenceSession? _session;
    public bool IsReady => _session is not null;

    public SileroVadService(string onnxPath)
    {
        if (!File.Exists(onnxPath))
        {
            Log.Warn($"Silero VAD model not found at {onnxPath}; VAD disabled.");
            return;
        }
        try
        {
            _session = new InferenceSession(onnxPath, new SessionOptions
            {
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                InterOpNumThreads = 1,
                IntraOpNumThreads = 1,
            });
            Log.Info("Silero VAD inputs:  " + string.Join(", ",
                _session.InputMetadata.Select(kv =>
                    $"{kv.Key}:{kv.Value.ElementType}[{string.Join(",", kv.Value.Dimensions)}]")));
            Log.Info("Silero VAD outputs: " + string.Join(", ",
                _session.OutputMetadata.Select(kv =>
                    $"{kv.Key}:{kv.Value.ElementType}[{string.Join(",", kv.Value.Dimensions)}]")));
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to load Silero VAD: {ex.Message}");
        }
    }

    /// <summary>
    /// Trim leading/trailing silence. Returns the original array if VAD is not
    /// loaded or the clip has no detectable speech.
    /// </summary>
    public float[] Trim(float[] samples, double threshold = 0.5, int paddingMs = 200)
    {
        if (_session is null || samples.Length < FrameSize * 2) return samples;

        var state = new float[2 * 1 * StateHidden];
        var context = new float[ContextSize]; // zero-initialised for first frame
        int firstSpeechFrame = -1;
        int lastSpeechFrame  = -1;
        int frameCount = samples.Length / FrameSize;
        float maxProb = 0f;
        var sampledProbs = new List<float>(capacity: 20);

        var srTensor = new DenseTensor<long>(new[] { (long)SampleRate }, Array.Empty<int>()); // scalar
        var framedInput = new float[ContextSize + FrameSize]; // 64 + 512 = 576

        for (int f = 0; f < frameCount; f++)
        {
            // Prepend the last 64 samples from the previous frame, then copy this frame's 512.
            Array.Copy(context, 0,             framedInput, 0,           ContextSize);
            Array.Copy(samples, f * FrameSize, framedInput, ContextSize, FrameSize);

            var input = new DenseTensor<float>(framedInput, new[] { 1, ContextSize + FrameSize });
            var stIn  = new DenseTensor<float>(state,       new[] { 2, 1, StateHidden });

            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor("input", input),
                NamedOnnxValue.CreateFromTensor("sr",    srTensor),
                NamedOnnxValue.CreateFromTensor("state", stIn),
            };

            using var results = _session.Run(inputs);
            var byName = results.ToDictionary(v => v.Name);
            var prob  = byName["output"].AsTensor<float>().ToArray()[0];
            state     = byName["stateN"].AsTensor<float>().ToArray();

            // Save the last 64 samples of this frame as context for the next call.
            Array.Copy(samples, f * FrameSize + FrameSize - ContextSize, context, 0, ContextSize);

            if (prob > maxProb) maxProb = prob;
            if (f < 20) sampledProbs.Add(prob);

            if (prob >= threshold)
            {
                if (firstSpeechFrame < 0) firstSpeechFrame = f;
                lastSpeechFrame = f;
            }
        }

        if (firstSpeechFrame < 0)
        {
            var sample = string.Join(",", sampledProbs.Select(p => p.ToString("F3")));
            Log.Info($"VAD: no speech detected (threshold={threshold:F2}, maxProb={maxProb:F3}, first20=[{sample}]); passing audio through.");
            return samples;
        }

        var padSamples = Math.Max(0, paddingMs) * SampleRate / 1000;
        var startSample = Math.Max(0, firstSpeechFrame * FrameSize - padSamples);
        var endSample   = Math.Min(samples.Length, (lastSpeechFrame + 1) * FrameSize + padSamples);
        var trimmedLen  = endSample - startSample;

        if (trimmedLen <= 0 || trimmedLen == samples.Length) return samples;

        var trimmed = new float[trimmedLen];
        Array.Copy(samples, startSample, trimmed, 0, trimmedLen);
        Log.Info($"VAD: trimmed {samples.Length} → {trimmedLen} samples ({startSample}..{endSample}).");
        return trimmed;
    }

    public void Dispose() => _session?.Dispose();
}
