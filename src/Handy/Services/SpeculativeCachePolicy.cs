namespace Handy.Services;

/// <summary>
/// Pure decision helper for additive prepass reuse. The prepass produces a
/// transcript of a snapshot of the recording at the moment of a natural
/// pause; at hotkey release we want to keep that text as a *prefix* and only
/// run ASR on the new tail of audio captured since the snapshot, then splice.
/// </summary>
public static class SpeculativeCachePolicy
{
    public static Decision Decide(int finalSamples, int snapshotSamples,
        SileroVadService.Analysis? tail, AppSettings settings)
    {
        if (!settings.BackgroundRecognitionEnabled || snapshotSamples <= 0 || finalSamples < snapshotSamples)
            return Decision.ColdPass;
        // A tail shorter than the VAD can even frame (post-roll jitter) carries
        // no word of its own; the prefix already covers the utterance.
        if (finalSamples - snapshotSamples <= SilenceFloorSamples ||
            tail?.Status == SileroVadService.SpeechStatus.Silence)
            return Decision.UsePrefixOnly;
        if (tail is null || tail.Status == SileroVadService.SpeechStatus.Uncertain ||
            tail.Samples.Length < MinReliableTailSamples)
            return Decision.ColdPass;
        return Decision.UsePrefixPlusTail;
    }
    private const int MinReliableTailSamples = 16000 / 2; // 500 ms
    private const int SilenceFloorSamples = 16000 / 10;   // 100 ms

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

}
