# ASR Latency Reduction Notes

This note summarizes the local latency investigation for Handy.NET dictation.
The goal is lower hotkey-release-to-paste latency without sending audio to a
cloud service and without bypassing transcript filtering or glossary correction.

## Live CPU saturation and engine tuning (2026-10-11)

The user approved trying the candidate. The first deployment cleanly replaced
PID 29972 with PID 16764. Logs verify hotkeys/tray before model loading,
4317 ms asynchronous model readiness, and a 306 ms startup warmup. A second
deployment (PID 28540) adds the measured engine changes below. Verified live:
hotkeys before model loading, 5489 ms model readiness, 271 ms warmup under heavy
CPU load, then an Idle heartbeat with Normal process priority restored.
The preceding live AGY dictation selected CtrlV after its process probe;
Windows accepted the chord, but user-visible receipt remains unconfirmed.
Rollback binaries and
pre-tuning settings are under `artifacts/dct-038/rollback-before-engine-tuning`.

The latest reported missed paste at 12:08:35 was explicitly `WrongWindow`:
dictation began in a console window, but Task Manager had focus when the 4560 ms decode
finished. This is a focus-policy refusal, not evidence that CPU saturation lost
the transcript. At 12:08:58 another injection targeted `Select <console>` and
Windows accepted every event. Console selection can suspend the receiving CLI;
accepted SendInput is not confirmation of receipt. Later direct console-state
inspection found QuickEdit enabled and selection flags zero, so the selection
had ended. The candidate keeps the focus guard and Direct recovery, and now
reports that text is saved with the configured recovery shortcut rather than
claiming clipboard success unconditionally. Automatic behavior after switching
away remains a pending user preference; no delayed paste queue was enabled.

Sampling confirmed 100% CPU use across 20 logical CPUs, with several Python
workers and Defender active. About 35 GiB RAM remained available. This supports
CPU contention in this incident, but does not exclude occasional paging or
attribute every stall to one process. No unrelated workload was stopped.

Alternating ABBA runs with two loaded models, same inputs and identical words:

| Speech clip | Four encoder threads | Eight encoder threads | Reduction |
|---|---:|---:|---:|
| 3 seconds | 718 ms | 550 ms | 23% |
| 7 seconds | 1225 ms | 1076 ms | 12% |
| 11 seconds | 1760 ms | 1380 ms | 22% |

Each cell is a four-pass median. Eight threads consumed more CPU time per
decode (about 27–54% more in these pairs) but finished sooner, matching the
user's latency priority. Reducing encoder threads to two or one and disabling
spinning was slower. Single-thread preprocessing/decoding also failed to
improve latency. The runtime remains sequential between operators, with four
auxiliary threads and spinning enabled.

Separate same-model ABBA priority tests showed Normal → AboveNormal medians
of 564 → 489 ms for 3-second clips and 2653 → 1742 ms for 11-second clips with
four encoder threads. With eight threads during a later, less-loaded period,
the gain was smaller: 270 → 254 ms and 779 → 724 ms. These results cannot be
combined into a promised overall speedup. The opt-in implementation raises
priority only during inference and restores Normal afterwards, including an
exceptional unwind. Existing non-Normal priorities are preserved.

Decoder input tensors and buffers are now reused within each transcription.
Logits are read without a managed array copy, output lookup avoids a dictionary
per step, and blank outputs do not copy LSTM state. Managed allocations dropped
from 1.61 to 0.85 MB on the 3-second clip and 5.27 to 2.74 MB on the 11-second
clip, roughly 48%. All compared words and punctuation matched the pre-change
decoder. The quiet final-two-seconds test at 0.005 gain also retained all words.
Phrase reuse still matched words, with punctuation differences; the final
repeat took 1145 ms total versus 1779 ms for growing-prefix decoding.

Machine settings are now `parakeetEncoderThreads: 8` and
`parakeetPrioritizeInference: true`; portable defaults remain 4 and false.
They take effect on restart. Encoder threads are capped to the machine's
logical processor count. Final tuned medians were 331/612/946 ms for 3/7/11
seconds, but those later runs had different system load and are not a clean
before/after comparison. Post-roll remains 200 ms to preserve final words.

All three regression suites pass, including actual priority restoration and
model-selection snapshot tests. Live AGY clipboard receipt and actual
sleep/wake acceptance remain open. Reproduce the offline investigations with:

```cmd
Handy.Recovery.Tests.exe --bench-threads <model-directory> <wav> contention
Handy.Recovery.Tests.exe --bench-threads <model-directory> <wav> extended
Handy.Recovery.Tests.exe --bench-encoder <model-directory> <wav>
Handy.Recovery.Tests.exe --bench-priority <model-directory> <wav> 4
Handy.Recovery.Tests.exe --bench-priority <model-directory> <wav> 8
Handy.Recovery.Tests.exe --bench-threads <model-directory> <wav> tuned
```

Raw and summarized results are retained in `artifacts/dct-038/` (not source
assets). Thread options follow the
[ONNX Runtime threading documentation](https://onnxruntime.ai/docs/performance/tune-performance/threading.html).

## Follow-up candidate and six performance investigations (2026-10-11)

These changes were first built and tested in an isolated output directory and
have now been deployed as recorded above. Actual sleep/wake and user-observed
paste acceptance remain open.

| Area | Candidate and evidence | Remaining limit |
|---|---|---|
| Startup | ASR and VAD construction run on a worker after tray, settings HWND and hotkey installation. Loading status is visible in tray/settings and on a premature capture request. Model requests serialize; stale/shutdown candidates are disposed. | Actual readiness still includes model load; live UI responsiveness needs verification. |
| Stop | Overlay/tray change before post-roll and session-marker writes. Stop sound stays after audio drain. | 200 ms remains configured; a single JFK clip cannot establish shorter-tail accuracy. |
| Silence tail | Explicit Speech/Silence/Uncertain status is separate from preserved audio. Real installed VAD on 100/200/1000/3000 ms digital silence selects prefix-only. Very short, missing-model, noisy or quiet uncertain tails retain full audio for decoding. | Silence shortcut is deliberately conservative: only near-digital silence with low VAD probability is discarded. |
| Repeated work | Finalization reads the completed cache after the gate and analyzes its new tail once. Full VAD runs only on fallback; uncertain-tail fallback preserves raw audio. Speculative passes decode new phrases with 800 ms lookback, once per new speech/pause. Glossary processing runs once at final assembly. | One native phrase benchmark preserves words but changes punctuation; broader dictation accuracy is unverified. |
| Stalls | Per-stage process CPU/wall time, allocation, GC counts/pause, working-set/private-byte deltas, page faults, system busy percentage and ASR dispatch/gate waits are recorded at Verbose level. Native counter reads avoid costly managed process enumeration. | Process-wide counters do not prove causation; faults include soft faults. Prior stalls predate this instrumentation. |
| UI work | Display retains at most 64 KiB, renders at most every 250 ms while the log page is visible, and does no hidden text-box work. Meter updates keep only the latest payload with one pending dispatcher operation. | Live long-session UI profiling remains. |

Foreground HWND is now captured at keyboard-hook receipt before dispatcher
queueing, and its metadata is read from that same handle before the overlay
appears. This removes a destination-capture race. Clipboard paste checks focus
again after clipboard access/settling, immediately before injecting its chord.
Direct also checks before the first character rather than waiting for character 16.

The installed `agy.exe` contains Go Bubble Tea/Bubbles symbols and a Windows
clipboard reader; the earlier Node/Gemini diagnosis does not describe this
current binary. Read-only process inspection confirms AGY is a direct child
of the classic console's `cmd.exe`. The candidate probes that relationship on
a worker after microphone capture starts. A known AGY target selects CtrlV
for normal dictation; unknown classic consoles retain configured Direct,
existing Windows Terminal routing remains, and Alt+Shift+V retains its Direct
recovery path for classic consoles. Explicit non-Direct settings are respected.
No window-title guessing, WMI, helper process or console attachment is involved.

Read-only native integration checks recognize AGY and return no override for
the separate M2 console or a different HWND. Probe wall time was 21–95 ms,
outside the stop/paste path; cached target validation took 0.1–1 ms. The owner
PID and AGY PID/name/start time are checked before using that prepared route.
The actual receiving behavior of AGY's CtrlV route is **not yet validated**;
the user's manual paste check is still pending. Its input library defaults to
[CtrlV clipboard paste](https://github.com/charmbracelet/bubbles/blob/v2.0.0/textarea/textarea.go),
but that alone does not prove this AGY build's bindings or delivery.

```cmd
Handy.Recovery.Tests.exe --probe-paste-target <hex-window-handle>
```

Mock injection regressions verify AGY clipboard routing, unknown-console
Direct, explicit None, Direct recovery, and a focus steal during paste delay.
They validate routing and injection decisions, not application receipt.

Offline optimized-graph benchmark, separate processes on installed Parakeet V2:

| Mode | Model construction | Source-hash/cache identity preparation |
|---|---:|---:|
| Original graphs | 5730 ms | 4394 ms (benchmark bookkeeping; absent from normal startup) |
| Serialize optimized graphs | 9357 ms | 5067 ms |
| Load optimized graphs, optimization disabled | 3952 ms | 3878 ms |

Three repeats each of 3/7/11-second speech inputs matched across all modes.
These are single construction samples under changing machine load. Including
hash preparation makes this cached path 7830 ms; this is **not** a demonstrated
end-to-end readiness improvement over normal startup. Caching remains an
offline benchmark option, not enabled by the tray app. Cache identity includes
source SHA-256 hashes, runtime assembly version, CPU identifier, provider and
session options. Hardware compatibility follows the
[ONNX Runtime graph optimization guidance](https://onnxruntime.ai/docs/performance/model-optimizations/graph-optimizations.html).

At natural JFK pause boundaries (2.70, 7.85, 11.00 seconds), repeatedly decoding
the growing prefix took 1824 ms total; decoding completed phrases with lookback
took 1167 ms. Final phrase work was 378 vs 864 ms. All words matched;
sentence punctuation differed. This is one paired audio example, not a normal
dictation median or a long-session accuracy result.
Repeat: 1644 ms growing-prefix work vs 1026 ms phrase work, again with the same
words and differing punctuation. The final two seconds attenuated to 0.005
amplitude also preserved all words after VAD in this one sample.

Simulated release 100 ms before the final audible sample: 200/100/50 ms capture
tails produced identical words on JFK. The 100 ms case retained the same input
length as the baseline, so this is a weak accuracy test and no reason to change
the live default. Actual quiet final words, domain terms and natural speech
need a larger corpus.

During initial benchmarks system busy measurements frequently reached
90–100%; encoder CPU time was about 3–4 times wall time, GC pauses were small,
and dispatch waits mostly negligible (one 22.8 ms example). A later run at
roughly 37–57% system busy had substantially shorter encoder times. Separate
process sampling found several CPU-active Python workers, Defender and VMware.
This supports contention as a contributor to **these runs**, not as a proven
explanation of the earlier 5.6-second encoder stall. No unrelated worker was
stopped; thread counts, CPU arenas and decoder buffer behavior were not tuned.

Reproducible offline commands (each cache mode in a fresh process):

```cmd
Handy.Recovery.Tests.exe --bench-model-cache <model-directory> <wav> <cache-directory> original
Handy.Recovery.Tests.exe --bench-model-cache <model-directory> <wav> <cache-directory> save
Handy.Recovery.Tests.exe --bench-model-cache <model-directory> <wav> <cache-directory> cached
Handy.Recovery.Tests.exe --bench-vad-tail <model-directory> <vad-onnx> <wav>
```

Release solution build with `--no-restore` has zero warnings/errors. The three
regression suites pass, including blocked model construction, latest-selection
replacement, construction failure, shutdown during load, actual speculative
phrase input coverage, repeated-pause suppression, bounded display logs,
conservative policy checks, warmup lifecycle and existing paste checks.
`heldMs` now ends at stop request; `postMs` includes transcript processing;
`vadMs` includes the actual finalization analysis work.

Live paste diagnosis remains separate: Direct is still selected. Logs show
explicit focus-change refusals as well as accepted input into a console whose
title began `Select`. A later read found QuickEdit enabled there but no active
selection. Accepted SendInput events do not establish text delivery. Do not
classify all of these as a paste-method regression or disable the focus guard
without identifying the failed destination.

## Initial startup / resume warmup investigation (2026-10-11)

The tray path loaded model sessions but never exercised inference before the
first dictation, and did not subscribe to Windows resume events. A background
one-second synthetic-silence decode now runs after startup, resume and model
replacement. Its output is discarded; it never touches history, clipboard,
glossary processing, task capture or text injection. The shared ASR semaphore
serializes it with real inference. Busy/missing-model requests are skipped,
duplicate in-flight requests coalesce, failures are logged without disabling
dictation, and shutdown/model replacement drain the worker before disposal.
There is no periodic keepalive. An immediate dictation can still briefly wait
for an already-started warm-up; it cannot interrupt native inference.

Initial measurements on the installed Parakeet V2 int8 model (first three
seconds of `jfk.wav`, Debug build, separate benchmark processes):

| Scenario | First decode | Warm-up outside dictation |
|---|---:|---:|
| Startup baseline | 323 ms | none |
| Startup with warm-up | 308 ms | 167 ms |
| Working-set eviction baseline | 315 ms | none |
| Working-set eviction with warm-up | 262 ms | 166 ms |

All twelve speech passes produced identical text. These single-run differences
are modest and do **not** establish that the user's noticeable delay is fixed.
Working-set eviction affects only the benchmark process and is a simulation,
not an actual sleep/wake test. The initial full 11-second clip likewise showed
no convincing gain (836 ms cold, 780 ms steady, 869 ms after warm-up).

Release-build repeat: startup 412 ms baseline versus 319 ms after a 195 ms
warm-up; eviction 332 ms baseline versus 331 ms after a 205 ms warm-up.
All twelve Release speech passes also matched. In particular, the eviction
benefit did not reproduce; live evidence is needed to attribute the symptom.

Run each mode in its own process using the recovery test executable:

```cmd
Handy.Recovery.Tests.exe --bench-warmup <model-directory> <wav> cold
Handy.Recovery.Tests.exe --bench-warmup <model-directory> <wav> warm
Handy.Recovery.Tests.exe --bench-warmup <model-directory> <wav> paged
Handy.Recovery.Tests.exe --bench-warmup <model-directory> <wav> paged-warm
```

Warm-up start/completion and elapsed time use the always-visible `Session:` log
channel. At Verbose level, `Flow: capture-start` separates foreground lookup,
overlay/setup, feedback and microphone startup; `Flow: asr-gate` measures lock
wait. Existing `Diag:` entries cover VAD, decode and paste. Actual live startup
and sleep/wake reproduction remain required before closing this investigation. No claim
about microphone recovery or Whisper performance follows from the Parakeet bench.

Follow-up verification on 2026-10-11: the isolated Release solution build passed
with zero warnings/errors using `--no-restore`; Handy.Tests,
Handy.Recovery.Tests and Handy.Diagnostics.Tests all passed. Build command:

```cmd
dotnet build Handy.sln --no-restore -c Release -p:BaseOutputPath=<isolated-output-directory>/
```

At that earlier checkpoint the live process ran from
`src/Handy/bin/Release/net8.0-windows/Handy.exe`. Its pre-fix log on October 11
records a first completed 18.642-second decode at 3857 ms and the next
10.108-second decode at 705 ms. Different audio lengths and speculative paths
prevent treating this as a controlled before/after comparison. The prior
startup also spent 31039 ms loading the encoder; inference warm-up does not
address that synchronous model-loading delay.

Remaining live checks, requiring the approved Handy.NET update/restart and a
normal sleep/wake cycle:

1. Verify the running executable matches the candidate and startup warm-up
   completes in the live log without adding a history entry or emitting text.
2. Measure the first and next comparable dictations after startup, including
   capture-start, ASR gate wait, decode and release-to-finished timings.
3. After actual sleep/wake, verify `reason=resume` completion, microphone
   capture and the same first/next dictation measurements.
4. Confirm the reported delay is reduced before marking this investigation complete;
   if it occurs before Listening, use the capture-start and startup timings
   to investigate that path rather than attributing it to decode.

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
after the snapshot. Confirmed silence can skip final ASR; short or uncertain
speech falls back to a full pass so final words are retained.

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
# CPU stress capture follow-up (2026-10-11)

Hotkey follow-up: WH_KEYBOARD_LL previously ran on the WPF dispatcher and performed foreground process/title inspection on every Space key, even when debug output was filtered. Diagnostic construction is now gated by enabled verbosity. The hook runs on its own background Dispatcher message thread; trigger/recovery/cancel actions marshal to WPF, and shutdown unhooks on the owning thread. An empty-chord lifecycle test verifies independent pumping and thread termination without injecting keys. All three suites pass. Real saturated-CPU hotkey reliability remains to be validated. Rollback files are in `artifacts/dct-038/rollback-before-hook-thread`.

Live CPU utilization reached 100% with 33.5 GiB available memory. A 29.25-second recording required 4214 ms ASR, including 3603 ms encoder time; gate/dispatch waits and GC pauses were negligible. Another recording logged 20.4 seconds held but 16.85 seconds captured, then an empty transcript. This suggests capture loss or delayed delivery, but does not establish its cause.

The microphone previously used NAudio's default three buffers at 50 ms each, only 150 ms reserve. The candidate uses twenty buffers (one second reserve) while retaining 50 ms delivery granularity. Each recording now logs actual capture elapsed time, live audio duration excluding pre-roll, shortfall and maximum callback gap. Extra reserve is a mitigation to validate under real contention, not proof of lossless capture. Release build and all three regression suites pass. Rollback files are in `artifacts/dct-038/rollback-before-capture-reserve`.

## Review follow-up — post-roll race and silent-tail verdict (11 October 2026, later)

Two findings from reading the 11 October `Diag:` / `Flow:` lines against the code:

1. **The silence verdict never fired on a real microphone.** `SileroVadService.Analyze`
   returned `Silence` only when every sample was digital zero. Mic noise is never
   zero, so every silent tail after a speculative snapshot came back `Uncertain`,
   the cache policy forced a cold pass, and the cold pass also skipped VAD trimming
   of the whole clip. Example at 16:20:14: a 29.25 s recording whose prefix was
   fully decoded, 4 s tail with VAD maxProb 0.075, decision `ColdPass` on all
   468 000 untrimmed samples, `asrMs=4214`. With the prefix it would have been
   roughly zero. Silence is now "best VAD frame under a third of the speech
   threshold"; the quiet-speech benchmark (JFK at -46 dB) still scores 0.56 and
   stays `Speech`. Cold passes always trim. A tail of 100 ms or less is treated
   as post-roll jitter and uses the prefix regardless.

2. **The 200 ms post-roll was dead time.** `stopMs` was 202-240 ms on every one of
   today's dictations and nothing ran during it. On hotkey release a speculative
   pass now snapshots the buffer so far, pads it with the post-roll's worth of
   zeros, and decodes while the post-roll elapses. After the latch the usual
   policy sees a ~200 ms tail: silent in the normal case, so the prefix is the
   transcript and the only wait is whatever is left of that decode; speech if the
   user released mid-word, in which case the tail is decoded as before. Expected
   effect for a short dictation: total drops by about the post-roll, roughly
   from 450-650 ms to 250-450 ms. Measure with the `Diag:` line (`specPrefix=true`
   and `stopMs` no longer preceding `asrMs`) and the new `release=true` field on
   `Spec:` lines.

Not done, needs a decision: both RTX 3090s sit idle while the encoder burns
2.7 CPU-seconds per 3 s clip on a saturated CPU. ONNX Runtime CUDA or DirectML
execution would make the encoder roughly length-independent, but needs a package
change, an fp16/fp32 model variant, and a CPU fallback for the work laptop.
