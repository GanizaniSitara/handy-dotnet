# ASR Latency Reduction Notes

This note summarizes the local latency investigation for Handy.NET dictation.
The goal is lower hotkey-release-to-paste latency without sending audio to a
cloud service and without bypassing transcript filtering or glossary correction.

## Current Long Pole

The `Diag:` line emitted per live dictation shows ASR inference as the dominant
stage. For representative live samples, short recordings spent milliseconds in
VAD, history, paste, and clipboard work, while ASR consumed the visible wait.

Observed examples:

- 2.1 s audio: `asrMs=2754`
- 25.25 s audio: `asrMs=3137`
- Parakeet after switch: 8.2 s audio, `asrMs=414`, total release-to-finished
  `574 ms`

Whisper model loading is not the per-dictation problem. The app creates and
keeps a warm `WhisperFactory` at startup; per-dictation `asrMs` is processor
creation plus decode.

## Backend And Model Findings

Using the one-shot file path on the bundled `jfk.wav` sample:

| Backend | Audio | Median ASR | RTF |
|---|---:|---:|---:|
| Parakeet V2 int8 | 11.0 s | 704 ms | 0.064 |
| Whisper base | 11.0 s | 3103 ms | 0.282 |
| Whisper tiny.en | 11.0 s | 1476 ms | 0.134 |

Parakeet is still the fastest local backend. If Parakeet accuracy is acceptable
after glossary correction, it is the lowest-latency choice. If Whisper is needed
for work vocabulary, `tiny.en` is the best immediate speed/accuracy candidate:
it is about 2.1x faster than Whisper base on the sample while staying on the
Whisper path that supports vocabulary prompting.

## Background Recognition

The experimental `backgroundRecognitionEnabled` setting starts ASR during a
natural pause while recording is still active. On hotkey release, the app uses
that completed pass as the transcript prefix and decodes only the tail captured
after the snapshot. If the tail is silence or too short, final ASR is skipped.

The additive path combines raw prefix and tail output first, then runs the
normal transcript filter and domain glossary once over the combined text. This
preserves corrections whose phrase crosses the splice boundary.

Bench command:

```cmd
Handy.exe --bench-additive path\to\clip.wav --split 0.70
```

On `jfk.wav` with Parakeet V2 and a split at the natural phrase pause:

| Design | Post-release ASR wait |
|---|---:|
| Cold full pass | 624 ms |
| Additive tail pass | 246 ms |
| Saved | 378 ms (60.6%) |

The spliced text differed only in punctuation at that split. Artificial splits
inside speech can produce worse text because the tail decoder loses left
context; the live trigger is pause-driven to avoid cutting mid-word.

## Current Recommendation

For lowest latency, run Parakeet with background recognition enabled and keep
the domain glossary populated. For higher vocabulary fidelity on Whisper, switch
to `tiny.en` before trying heavier background/streaming work.

Keep `backgroundRecognitionEnabled` experimental until several live dictations
confirm that pause-triggered splicing does not degrade normal work transcripts.
