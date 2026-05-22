namespace Handy.Services;

/// <summary>
/// Pure decision helper for additive prepass reuse. The prepass produces a
/// transcript of a snapshot of the recording at the moment of a natural
/// pause; at hotkey release we want to keep that text as a *prefix* and only
/// run ASR on the new tail of audio captured since the snapshot, then splice.
/// </summary>
public static class SpeculativeCachePolicy
{
    private const int SampleRate = 16000;

    // Below this, the tail after the snapshot is effectively silence (the pause
    // that triggered the snapshot plus post-roll), so the prefix transcript
    // already covers the whole utterance.
    private const int SilenceFloorSamples = SampleRate / 10;   // 100 ms

    // A tail that contains real speech but is shorter than this is too short to
    // decode reliably on its own; re-decode the whole buffer (ColdPass) rather
    // than risk a bad or dropped tail. At or above it, decode just the tail.
    private const int MinReliableTailSamples = SampleRate / 2; // 500 ms

    public enum Decision
    {
        /// <summary>No usable prepass — run full ASR on the final buffer.</summary>
        ColdPass,

        /// <summary>Prepass covers (effectively) the entire final buffer — the
        /// tail since the snapshot is silence. Use the prepass text verbatim and
        /// skip ASR entirely.</summary>
        UsePrefixOnly,

        /// <summary>Prepass covers most of the buffer; the user said more after
        /// the snapshot. Use the prepass text as a prefix and run ASR only on
        /// the new tail of audio, then splice.</summary>
        UsePrefixPlusTail,
    }

    /// <summary>
    /// Decide how to assemble the final transcript given a cached prepass and
    /// the final captured audio.
    /// </summary>
    /// <param name="finalRawSampleCount">Length of the final captured buffer (raw, pre-VAD).</param>
    /// <param name="snapshotRawSampleCount">Length of the raw buffer at the moment the prepass snapshot was taken.</param>
    /// <param name="tailVadSampleCount">Length of the tail (raw audio after the snapshot) after VAD trim. Pass 0 if not yet computed.</param>
    /// <param name="settings">Current app settings.</param>
    public static Decision Decide(
        int finalRawSampleCount,
        int snapshotRawSampleCount,
        int tailVadSampleCount,
        AppSettings settings)
    {
        if (!settings.BackgroundRecognitionEnabled) return Decision.ColdPass;
        if (snapshotRawSampleCount <= 0) return Decision.ColdPass;

        // Sanity: final buffer should never be materially shorter than the
        // snapshot (capture is append-only). If it somehow is, fall back to
        // a cold pass on whatever we actually have.
        if (finalRawSampleCount + SilenceFloorSamples < snapshotRawSampleCount) return Decision.ColdPass;

        var tailRawSamples = finalRawSampleCount - snapshotRawSampleCount;

        // Tail is effectively silence — the user trailed off after the last
        // pause, so the prefix already covers the whole utterance.
        if (tailRawSamples <= SilenceFloorSamples || tailVadSampleCount <= SilenceFloorSamples)
            return Decision.UsePrefixOnly;

        // Tail has real speech but is too short to trust as a standalone decode.
        // Re-decode the full buffer so the final words are never dropped — this
        // costs the speculative speed-up for this one dictation only.
        if (tailVadSampleCount < MinReliableTailSamples)
            return Decision.ColdPass;

        // Substantial tail: keep the prefix and decode only the new tail.
        return Decision.UsePrefixPlusTail;
    }
}
