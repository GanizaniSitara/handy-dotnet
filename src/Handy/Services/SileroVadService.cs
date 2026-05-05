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
    ///
    /// <paramref name="trailingHangoverMs"/> extends <c>lastSpeechFrame</c> through
    /// up to that many ms of sub-threshold frames after the last clear speech
    /// frame, so a soft trailing release (the closing consonant of a word the
    /// speaker trails off on) isn't aggressively cut. Refreshes whenever a
    /// supra-threshold frame is seen, so mid-utterance pauses still extend
    /// through to the next speech burst (preserving Trim's "edges only" semantic).
    /// </summary>
    public float[] Trim(float[] samples, double threshold = 0.5, int paddingMs = 200, int trailingHangoverMs = 480)
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

        var frameMs = FrameSize * 1000 / SampleRate; // 32 ms
        var hangoverFrames = Math.Max(0, trailingHangoverMs) / Math.Max(1, frameMs);
        int hangoverRemaining = 0;

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
                hangoverRemaining = hangoverFrames;
            }
            else if (firstSpeechFrame >= 0 && hangoverRemaining > 0)
            {
                // Soft trailing frame: keep extending the tail so the closing
                // release of a quiet word isn't chopped before the padding window.
                lastSpeechFrame = f;
                hangoverRemaining--;
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
        Log.Info($"VAD: trimmed {samples.Length} → {trimmedLen} samples ({startSample}..{endSample}; pad={paddingMs}ms, tailHangover={trailingHangoverMs}ms).");
        return trimmed;
    }

    /// <summary>
    /// Streaming-style trim that drops silence both at the edges AND in the
    /// middle of the clip. Mirrors upstream Handy's <c>SmoothedVad</c>
    /// (audio_toolkit/vad/smoothed.rs): a small state machine with onset
    /// confirmation, a hangover tail, and a rolling prefill buffer. When
    /// speech onsets, the prefill window is flushed first so the model still
    /// sees a few hundred ms of context before the first detected voice frame.
    ///
    /// Use this for the live recording path. <see cref="Trim"/> is the older
    /// "first..last + padding" behaviour kept for the --transcribe-file
    /// one-shot, where mid-clip pauses are part of the asset and shouldn't
    /// be removed.
    /// </summary>
    public float[] Smooth(
        float[] samples,
        double threshold = 0.3,
        int prefillFrames = 14,
        int hangoverFrames = 14,
        int onsetFrames = 2)
    {
        if (_session is null || samples.Length < FrameSize * 2) return samples;
        if (prefillFrames  < 0) prefillFrames  = 0;
        if (hangoverFrames < 0) hangoverFrames = 0;
        if (onsetFrames    < 1) onsetFrames    = 1;

        var state = new float[2 * 1 * StateHidden];
        var context = new float[ContextSize];
        var srTensor = new DenseTensor<long>(new[] { (long)SampleRate }, Array.Empty<int>());
        var framedInput = new float[ContextSize + FrameSize];

        int frameCount = samples.Length / FrameSize;
        var output = new List<float>(samples.Length);
        var prefill = new Queue<int>(prefillFrames + 1);

        int onsetCounter = 0;
        int hangoverCounter = 0;
        bool inSpeech = false;
        int speechFrames = 0;
        int noiseFrames = 0;
        float maxProb = 0f;

        for (int f = 0; f < frameCount; f++)
        {
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
            var prob   = byName["output"].AsTensor<float>().ToArray()[0];
            state      = byName["stateN"].AsTensor<float>().ToArray();

            Array.Copy(samples, f * FrameSize + FrameSize - ContextSize, context, 0, ContextSize);

            if (prob > maxProb) maxProb = prob;
            var isVoice = prob >= threshold;

            // Buffer every frame for possible pre-roll on the next onset.
            prefill.Enqueue(f);
            while (prefill.Count > prefillFrames + 1) prefill.Dequeue();

            switch ((inSpeech, isVoice))
            {
                // Potential start of speech — wait for `onsetFrames` consecutive
                // voice frames before flipping into speech mode. This rejects
                // single-frame spikes (cough, click) that aren't real speech.
                case (false, true):
                    onsetCounter++;
                    if (onsetCounter >= onsetFrames)
                    {
                        inSpeech = true;
                        hangoverCounter = hangoverFrames;
                        onsetCounter = 0;
                        // Flush the prefill window so the transcriber sees the
                        // ~prefillFrames * 32 ms of audio that immediately
                        // preceded the first confirmed speech frame.
                        foreach (var pf in prefill)
                            AppendFrame(output, samples, pf);
                        speechFrames += prefill.Count;
                    }
                    else
                    {
                        noiseFrames++;
                    }
                    break;

                // Continuing speech — refresh the hangover counter so brief
                // single-frame dips don't break the segment.
                case (true, true):
                    hangoverCounter = hangoverFrames;
                    AppendFrame(output, samples, f);
                    speechFrames++;
                    break;

                // In speech but the current frame is silence. Keep emitting
                // until hangover expires; then close the segment. The next
                // (false, true) transition will reopen it with fresh prefill.
                case (true, false):
                    if (hangoverCounter > 0)
                    {
                        hangoverCounter--;
                        AppendFrame(output, samples, f);
                        speechFrames++;
                    }
                    else
                    {
                        inSpeech = false;
                        noiseFrames++;
                    }
                    break;

                // Silence — reset onset accumulator so non-consecutive voice
                // frames can't sneak past the onset threshold.
                case (false, false):
                    onsetCounter = 0;
                    noiseFrames++;
                    break;
            }
        }

        if (output.Count == 0)
        {
            Log.Info($"VAD smooth: no speech detected (threshold={threshold:F2}, maxProb={maxProb:F3}); passing audio through.");
            return samples;
        }

        var arr = output.ToArray();
        Log.Info($"VAD smooth: {samples.Length} → {arr.Length} samples (speech={speechFrames}, noise={noiseFrames} frames; threshold={threshold:F2}, prefill={prefillFrames}, hangover={hangoverFrames}, onset={onsetFrames}).");
        return arr;
    }

    private static void AppendFrame(List<float> output, float[] samples, int frameIndex)
    {
        var start = frameIndex * FrameSize;
        for (int i = 0; i < FrameSize; i++)
            output.Add(samples[start + i]);
    }

    public void Dispose() => _session?.Dispose();
}
