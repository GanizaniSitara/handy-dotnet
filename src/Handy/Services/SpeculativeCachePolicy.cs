namespace Handy.Services;

/// <summary>
/// Pure decision helper for the speculative-recognition cache reuse rule.
/// Extracted so the cache-hit logic is testable without spinning up audio
/// capture, ONNX sessions, or background tasks.
/// </summary>
public static class SpeculativeCachePolicy
{
    private const int SampleRate = 16000;

    /// <summary>
    /// Decide whether a cached speculative transcript is close enough to the
    /// final VAD-trimmed buffer to reuse on hotkey release.
    ///
    /// Hit when the cached snapshot's VAD-trimmed length is within
    /// <c>BackgroundMinNewSpeechMs</c> samples of the final VAD-trimmed length
    /// AND the cache completed within <c>BackgroundCacheMaxStaleMs</c>.
    ///
    /// The length tolerance is keyed to <c>BackgroundMinNewSpeechMs</c> rather
    /// than a hardcoded 1 s so the threshold tracks the trigger gap: if a
    /// snapshot fired ≥1.5 s into the buffer and no new snapshot has been
    /// triggered since, the trimmed-length delta must stay under that same
    /// 1.5 s — otherwise the user said new words after the snapshot.
    /// </summary>
    public static bool ShouldReuseCache(
        int finalVadSampleCount,
        int cacheVadSampleCount,
        long cacheAgeMs,
        AppSettings settings)
    {
        if (!settings.BackgroundRecognitionEnabled) return false;
        if (cacheVadSampleCount <= 0) return false;
        if (cacheAgeMs > settings.BackgroundCacheMaxStaleMs) return false;

        var deltaSamples = System.Math.Abs(finalVadSampleCount - cacheVadSampleCount);
        var deltaMsAllowed = System.Math.Max(0, settings.BackgroundMinNewSpeechMs);
        var deltaSamplesAllowed = deltaMsAllowed * SampleRate / 1000;
        return deltaSamples <= deltaSamplesAllowed;
    }
}
