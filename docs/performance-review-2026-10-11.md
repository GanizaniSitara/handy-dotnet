# Handy.NET performance review — 11 October 2026

Reviewed source at `c1a71f8` plus the existing uncommitted warm-up changes, live settings, running process, and local logs. This review does not change application behavior or settings. Source locations below refer to that checkout.

## Evidence and limits

The running app is the Release executable under `src/Handy/bin/Release/net8.0-windows`, using Parakeet V2 int8. Background recognition is enabled; post-roll is 200 ms. A new startup warm-up completed at 10:59:12 in 226 ms. The complete dictations below precede that restart, so they do not measure its effect.

Ten complete successful dictation records on 11 October, before the restart:

| Stage | Median | Range |
|---|---:|---:|
| Stop/audio tail | 212.5 ms | 202–238 ms |
| Reported VAD | 81.5 ms | 14–317 ms |
| Reported ASR | 711.5 ms | 236–6,976 ms |
| Reported paste | 41.5 ms | 13–116 ms |
| Stop-handler entry to completion | 1,161 ms | 480–7,642 ms |

Three of those ten dictations reused a speculative prefix; none avoided final ASR entirely. These are different utterances under uncontrolled load, not an A/B benchmark. Stage medians do not add to the total median, and instrumentation has gaps described below.

Encoder construction took 31,039 ms on 10 October at 17:16, and 3,971 ms on 11 October at 10:59. The corresponding application-started messages appeared roughly 36 and 5 seconds after the previous-session marker. Those are approximate logged initialization intervals, not process-launch stopwatch measurements.

The broader two-day logs contain much larger total-time outliers with unexplained gaps. Recent commits include a UI deadlock fix, so those older incidents must not be treated as proof of a current regression.

## Ranked findings

### 1. High: startup and model changes synchronously load models on the UI thread

`App.xaml.cs:202` constructs ASR before microphone initialization, settings window, tray and hook installation. `ParakeetTranscriptionService.cs:63` enables full graph optimization and constructs three sessions sequentially. `App.xaml.cs:1348` also replaces models synchronously from the settings path.

This directly makes the measured multi-second load part of an unresponsive startup/model-change experience. The new inference warm-up happens after initialization and cannot remove this delay.

Recommended implementation: present the tray/settings/loading state first, construct models on a worker, and publish a ready model atomically. Disable recording until ready, or explicitly buffer audio while loading with cancellation and memory limits. Do not silently lose a hotkey press. Preserve the existing foreground-window handle requirement when simplifying the settings UI.

Separately benchmark caching optimized graphs rather than optimizing the original graph on every launch. ONNX Runtime supports offline graph serialization; cache identity must include model hash, runtime version, provider/options and hardware compatibility. This may shorten actual readiness time; asynchronous loading mainly improves responsiveness. No quantified cache saving is established yet. [ONNX Runtime graph optimization documentation](https://onnxruntime.ai/docs/performance/model-optimizations/graph-optimizations.html).

### 2. High: stop feedback waits behind a fixed 200 ms audio tail

`App.xaml.cs:718` awaits `StopAsync(PostRollMs)` before changing the overlay or playing stop feedback at lines 762–763. `AudioCaptureService.cs:164` explicitly delays. Even a very fast decode cannot remove this floor.

Show a finishing state immediately on stop while capture drains. An audible cue before capture ends needs care because the microphone can record it. Then A/B 50–100 ms or speech-aware post-roll against 200 ms, retaining sufficient buffer margin and checking final consonants. This could remove roughly 100–150 ms from every completed dictation if accuracy holds; it is an estimate, not a measured improvement. Immediate visual acknowledgment can improve perceived responsiveness without changing captured audio.

### 3. High: silence detection and speculative cache reuse disagree

`SileroVadService.cs:132` returns the original audio when no speech is detected. However, `App.xaml.cs:813` passes the returned array length as the amount of tail speech to `SpeculativeCachePolicy.Decide`. That policy treats <=100 ms as silence and 100–500 ms as an unreliable speech fragment requiring full decoding.

Consequently a silent 200 ms tail can trigger a full decode, and a longer silent tail can trigger tail decoding. The silence-based prefix-only shortcut cannot work as intended for these cases. This is a confirmed interface mismatch, independent of machine speed; the logs do not establish how often it accounts for a user's delay.

Return explicit VAD analysis such as speech detected, speech bounds and confidence/status separately from the conservative audio-preserving trim result. Preserve the full-clip fallback for quiet or uncertain speech. Test actual silence and quiet final words through VAD plus cache policy together. Current tests at `tests/Handy.Tests/Program.cs:344` supply a synthetic zero-length VAD result that this no-speech branch does not produce.

### 4. Medium/high: finalization rescans audio unnecessarily

`App.xaml.cs:785` VAD-trims the whole recording before deciding whether only a cached prefix plus tail is needed. Tail VAD then runs before the ASR gate at line 828, again after the gate at line 858 even if the cache has not changed, and again with overlap at line 885.

Resolve the cache first; analyze the required tail once and reuse the result if the cache version is unchanged. Only analyze the full clip when a full decode is required, and retain the 800 ms overlap for splice accuracy. Streaming speech-boundary tracking could move more VAD work into recording. Savings will depend on clip length and cache use; existing `vadMs` excludes these additional tail scans.

### 5. Medium/high: background recognition repeatedly decodes an expanding prefix

`RunSpeculativePass` snapshots, trims and transcribes the whole recording-so-far each time. The minimum-new-speech setting actually measures new audio samples, including silence. Pause detection uses visually scaled level bars rather than speech probability. Sustained noise can prevent useful passes; long pauses can cause redundant ones. Each pass takes the same ASR gate needed at release.

First require new speech since the last snapshot and prevent repeated work during one continuing pause. Record speculative gate occupancy and useful-prefix coverage. Longer term, decode completed phrases once and process a bounded tail, retaining overlap, punctuation handling and one final glossary pass. This reduces repeated work but changes segmentation and needs transcript accuracy evaluation. Simply triggering more frequently could make contention worse.

### 6. Medium: stalls need attribution before engine tuning

At 10:57, a 12.7-second capture spent 5,626 ms in the encoder and 1,187 ms in decoder work. At 10:51, an approximately 8.8-second ASR input spent 588 ms in the encoder and 30 ms in decoding. VAD and preprocessing also slowed during the later incident. This establishes a broad slowdown, not its cause.

The live machine has 10 physical/20 logical CPUs. A later snapshot showed about 35.6 GB available RAM and Handy using about 846 MB working set; that cannot establish memory/CPU conditions during the earlier stalls.

Capture CPU time versus wall time, GC pauses/allocation, working-set/page-fault changes, queue waits and concurrent machine load around a slow dictation. Benchmark per-session thread counts: currently preprocessor, encoder and decoder all get four intra-op threads. One thread for small decoder operations and a measured encoder thread count are candidates. ONNX Runtime documents thread-pool and spinning tradeoffs; no specific setting is proven faster here. [Thread management](https://onnxruntime.ai/docs/performance/tune-performance/threading.html).

Do not blindly re-enable CPU arenas: the code deliberately disabled them to control retained memory. Evaluate any allocation/arena change with long-session memory measurements as well as latency.

### 7. Medium: avoidable decoder allocations; lower expected normal-case benefit

`ParakeetTranscriptionService.cs:190` allocates tensor wrappers, input lists, a name dictionary, logits and both LSTM state arrays on every inner decoding step. It copies state even for blank tokens whose state is discarded.

Reuse inputs and buffers, avoid the dictionary/full-logit copy, and only commit/copy recurrent state when needed. Evaluate the supported OrtValue API for fewer managed allocations. Normal decoder times around 30 ms indicate a smaller everyday opportunity than encoder/startup work; this is not a basis for promising a large total speedup. [ONNX Runtime C# API guidance](https://onnxruntime.ai/docs/tutorials/csharp/basic_csharp.html).

### 8. Medium: hidden UI logging grows for the entire session

`MainWindow.xaml.cs:35` attaches the log sink immediately. Lines 446–454 enqueue, append and scroll each displayed line even while settings are hidden; there is no text-length cap. `RecordingOverlay.SetLevels` also enqueues every audio update rather than coalescing to the newest value.

Keep a bounded log buffer, batch rendering while the log panel is visible, and coalesce meter updates. This removes an identified source of accumulating UI work; profiling is still needed to show its contribution to stalls. Also measure synchronous session-marker writes on phase changes before moving them off the critical path, preserving crash evidence.

## Measurement corrections and warm-up assessment

- `heldMs` is calculated at final completion, so it includes transcription/paste time. It is not the duration the hotkey was held.
- `postMs` times trailing-space handling after filtering/glossary work, not the actual postprocessing.
- `vadMs` only times the first full trim, omitting repeated tail analysis.
- ASR-gate wait is newly logged as a separate Flow line but absent from the per-dictation summary. UI dispatch, marker writes and other gaps also need attribution.
- `pasteMs` contains clipboard time that is reported separately as `copyMs`; blindly adding the fields double-counts it.

Add a dictation ID and monotonic timestamps for hotkey receipt, first visible response, first audio block, stop request, gate entry/exit, text ready, paste completion and UI idle. Compare time-to-ready, time-to-first-feedback and stop-to-paste separately.

Existing warm-up tests pass. The previous investigator's benchmark notes show mixed, modest changes and no reproducible working-set-eviction benefit. Today's 226 ms startup warm-up confirms execution, not a user-visible improvement. An immediately following dictation can wait for an already-running warm-up, and `Dispose` synchronously drains it during model change/shutdown. Keep warm-up subordinate to real work and measure actual first-use and resume behavior.

## Suggested delivery order and validation

1. Correct timing boundaries and add actual VAD/cache integration coverage.
2. Immediate stop feedback and responsive asynchronous model loading.
3. Silence-tail cache fix and removal of duplicate VAD work.
4. Bound hidden UI logging and speculative work.
5. Benchmark optimized-model caching, thread settings and decoder buffer reuse.
6. Consider a different model/provider only after these results; preserve local processing and glossary behavior.

Use fixed short/medium/long clips, silence tails, quiet final words, domain terms and natural pauses. Report transcript differences plus median/p95 latency over repeats, including idle, concurrent load, fresh process and real sleep/resume. A single JFK recording is insufficient to establish dictation accuracy.

Validation performed: the Release Handy fixture passed (13 correction cases plus settings, prompts, speculative policy, splicing, task capture and paste checks); Release recovery, hook, admission and warm-up lifecycle/concurrency checks passed. No new performance benchmark, real sleep/resume experiment or application change was performed for this review.
