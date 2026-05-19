# handy-dotnet

A native Windows .NET 8 port of the excellent
[**Handy**](https://github.com/cjpais/Handy) dictation app by
[@cjpais](https://github.com/cjpais) and contributors.

> **👉 If you can run the original, use it.** Upstream Handy
> ([handy.computer](https://handy.computer) · [GitHub](https://github.com/cjpais/Handy))
> is the canonical version — actively developed, cross-platform, and feature-complete.
> All the good design decisions here are theirs; any rough edges are ours.
>
> This port exists because we needed a .NET-based build for a specific
> deployment setup where that toolchain fits better than the upstream's.
> If that happens to suit you too, this repo may be useful — otherwise please
> use upstream and support that project.

Same UX as upstream — global hotkey → local speech-to-text → pasted into the
active window. Parakeet remains the default backend, reading the same
`parakeet-tdt-0.6b-v*-int8/` directory layout upstream ships. Whisper is also
available as a local GGML/whisper.cpp backend through Whisper.net.
Settings file, history, and on-disk Parakeet models are cross-compatible. The
port also includes a local domain glossary for business terms, optional Whisper
vocabulary prompting from that glossary, and clipboard recovery options for
terminal/VDI paste failures.

**Upstream stack:** Rust + Tauri + WebView2 + React frontend + `transcribe-rs`.
**This port:** .NET 8 WPF + NAudio + Microsoft.ML.OnnxRuntime.

## Why port it

The port keeps the upstream feature set while building on the standard
.NET toolchain:

- `.NET 8 SDK` as the only prerequisite to build.
- Runtime is the in-box `.NET 8 Desktop Runtime` — or publish self-contained
  and drop the folder.

## Differences from upstream Handy

This port has drifted into a Windows-flavoured branch of the design. The core
loop (hotkey → capture → ASR → paste) is identical. The differences worth
calling out:

**Added in this port:**

- **Direct paste mode with per-app delay tuning.** A `Direct` paste method
  injects each character via `KEYEVENTF_UNICODE` SendInput, which avoids
  the clipboard entirely. Useful for terminals and shells that swallow
  `Ctrl+V`. A per-char delay is exposed so the throttle can be tuned per
  environment.
- **Citrix auto-detection.** When the foreground window belongs to a
  Citrix client (`CDViewer`, `wfica32`, `Receiver`, `SelfService`,
  `CitrixWorkspaceApp`, `Workspace`), Direct paste uses a separate
  per-char delay so the client's keystroke forwarding doesn't drop or
  reorder Unicode events. Local apps keep the fast (zero-delay) path.
- **Copy-last-transcript hotkey** (default `Ctrl+Alt+Shift+Space`) puts
  the most recent transcript back on the clipboard. Recovery path when a
  paste fails silently — common in VDI / Citrix / integrity-level-mismatch
  terminal windows.
- **Domain glossary with context.** Beyond the upstream filler/stutter
  filter, an editable rule set rewrites mistranscribed business terms to a
  canonical form, with optional required-context and blocked-context
  predicates per rule.
- **Whisper vocabulary prompting** built from enabled glossary canonical
  terms. Whisper-only (Parakeet's current ONNX greedy decoder has no
  prompt input). Opt-in; logged when active.
- **Speculative / additive recognition.** Detects natural pauses during
  recording and runs an ASR pass on the audio captured so far; on
  hotkey-release only the tail since the snapshot is decoded, then
  spliced. Trades a little CPU for noticeably shorter perceived wait on
  long dictations. Experimental, off by default.
- **Parakeet variant selector** in Settings — `Auto / V2 / V3`. Auto
  prefers v3 when installed; explicit choice forces that variant. V2 is
  faster, V3 is generally more accurate on hard audio.
- **Log verbosity tiers.** Quiet / Normal / Verbose / Debug, applied
  independently to the in-app Log panel and the on-disk log file. Quiet
  gives just text-in / text-out; Debug adds the per-keypress firehose.
- **Git-derived version stamp.** Every build's title bar and footer carry
  `<semver>+<commit-count>.g<sha>[.dirty]` so you can tell at a glance
  which build you're running.
- **Benchmarks** for ASR latency, vocabulary biasing A/B, WER against a
  parallel-history baseline, and additive background recognition. Living
  under `bench/` and `docs/asr-*.md`.

**Not (yet) in this port:**

- **Cross-platform.** Windows-only. Upstream supports macOS and Linux.
- **LLM post-processing.** Upstream can route transcripts through an
  OpenAI-compatible API for cleanup; this port does not.
- **Theme-aware tray icons.** This port uses a single palette.
- **Proper TDT duration-head consumption.** Both consume the encoder /
  decoder / joiner outputs, but this port currently uses vocab logits
  only for greedy decoding rather than the full TDT duration head. Output
  is correct for the supported model set; the deferred work is in
  [FEATURES.md](FEATURES.md).

## Framework choice: WPF over WinUI 3

- WPF runs on the in-box `Microsoft.WindowsDesktop.App` runtime and publishes as a plain unpackaged `.exe`. WinUI 3 requires the Windows App SDK bootstrapper and MSIX packaging, which adds runtime prerequisites we preferred to avoid.
- WPF hosts the WinForms `NotifyIcon` tray directly via `UseWindowsForms=true`, so no third-party tray package.

## Transcription backends

Upstream Handy supports two backends: Whisper (GGML) and NVIDIA NeMo Parakeet
TDT (ONNX). This port supports both, with `TranscriptionBackend` in
`%APPDATA%\Handy\settings.json` selecting `"Parakeet"` or `"Whisper"`.
Parakeet is the default.

The Parakeet path uses the same model directory layout, same int8 quantized
weights, same 16 kHz mono input, and same greedy RNN-T decoding that
transcribe-rs uses. That means:

- Reuse of the exact model files already cached by upstream Handy on the same machine.
- No audio round-trip through a separate Python/Rust/GPU stack.
- ~500 ms inference for ~10 s of audio on CPU (one thread) with the V2 int8 model.

The Whisper path uses Whisper.net/whisper.cpp with GGML models. Set
`WhisperModel` to `"tiny.en"`, `"tiny"`, `"base"`, `"base.en"`, `"small"`,
or `"small.en"`; models live under
`%APPDATA%\Handy\models\whisper\` as `ggml-{model}.bin`.

When the backend is Whisper, the Models tab can enable a vocabulary prompt built
from enabled domain-glossary canonical terms. This is a recognizer-level bias
using Whisper.net's initial-prompt support. It is opt-in, logged when active,
and does not affect Parakeet, whose current ONNX greedy decoder has no prompt or
hotword input. See [`docs/asr-vocabulary-biasing.md`](docs/asr-vocabulary-biasing.md)
for the evaluation path and Parakeet fine-tuning recommendation.

Pipeline summary:

```
waveforms [N,16kHz]
        │
        ▼  nemo128.onnx                  (mel-spectrogram preprocessor)
mel features [1, 128, T_frames]
        │
        ▼  encoder-model.int8.onnx       (Conformer encoder)
encoded [1, 1024, T_enc]   (T_enc ≈ T_frames / 8, 80 ms per frame)
        │
        ▼  decoder_joint-model.int8.onnx (RNN-T decoder + joiner)
greedy loop → token ids → vocab.txt → text
```

## Build

### Prerequisites

- **.NET 8 SDK** — verify with `dotnet --list-sdks`. User-scope install:
  ```powershell
  iwr https://dot.net/v1/dotnet-install.ps1 -OutFile dotnet-install.ps1
  .\dotnet-install.ps1 -Channel 8.0 -Quality GA -InstallDir $env:USERPROFILE\.dotnet
  ```
  …then call `%USERPROFILE%\.dotnet\dotnet.exe` directly, or add that folder to `PATH`.
- **A model** — Parakeet by default; Whisper models can be downloaded from the
  Models tab or pulled on first run when the Whisper backend is selected.

### From the CLI

```cmd
git clone https://github.com/GanizaniSitara/handy-dotnet.git
cd handy-dotnet
dotnet restore
dotnet build -c Release
dotnet run --project src\Handy\Handy.csproj -- --show
```

Self-contained single-folder publish:

```cmd
dotnet publish src\Handy\Handy.csproj -c Release -r win-x64 -p:SelfContained=true -o publish
```

### From Visual Studio

Open `Handy.sln`, set `Handy` as startup, F5.

## Parakeet model

The app auto-discovers a model directory, searching in order:

1. `%HANDY_MODEL_DIR%` (environment override).
2. `%APPDATA%\Handy\models\parakeet-tdt-0.6b-v{3,2}-int8\`.
3. Upstream Handy's cache: `%APPDATA%\com.pais.handy\models\parakeet-tdt-0.6b-v{3,2}-int8\`.

If you already ran upstream Handy and downloaded Parakeet through it, **the .NET
port uses the same files automatically.**

A Parakeet model directory contains:

```
parakeet-tdt-0.6b-v2-int8/
├── config.json                    { "model_type": "nemo-conformer-tdt", "features_size": 128, "subsampling_factor": 8 }
├── nemo128.onnx                   mel-spectrogram preprocessor
├── encoder-model.int8.onnx        ~650 MB Conformer encoder
├── decoder_joint-model.int8.onnx  ~9 MB decoder + joiner
└── vocab.txt                      ~1025 SentencePiece tokens
```

Direct download URLs are in the upstream README (`https://blob.handy.computer/parakeet-v{2,3}-int8.tar.gz`); extract the archive into `%APPDATA%\Handy\models\`.

## Whisper model

Set `TranscriptionBackend` to `"Whisper"` in settings or use the Models tab.
The selectable sizes are `"tiny"`, `"base"`, and `"small"`; the default is
`"base"`. Handy stores these files at:

```
%APPDATA%\Handy\models\whisper\ggml-tiny.bin
%APPDATA%\Handy\models\whisper\ggml-tiny.en.bin
%APPDATA%\Handy\models\whisper\ggml-base.bin
%APPDATA%\Handy\models\whisper\ggml-base.en.bin
%APPDATA%\Handy\models\whisper\ggml-small.bin
%APPDATA%\Handy\models\whisper\ggml-small.en.bin
```

The Models tab can download them through Whisper.net. If the selected file is
missing at startup, Handy marks the Whisper backend as not ready; use the
Download button or place the file manually, then apply the setting again.

## Usage

1. Run `Handy.exe`. A tray icon appears. Default hotkey: **Ctrl + Alt + Space**.
   - `--show` opens settings at launch.
   - `--start-hidden` forces tray-only.
   - `--no-tray` hides the tray icon.
2. Put the cursor in any text field.
3. Press the hotkey. Speak. Press it again (toggle) or release (if push-to-talk is on).
4. The transcript goes to the clipboard and is pasted via the configured paste method.
5. If paste fails, use the tray menu or **Ctrl + Alt + Shift + Space** to copy
   the last transcript back to the clipboard.

Useful settings:

- **Always copy transcription to clipboard** keeps every successful transcript
  on the clipboard after paste.
- **Domain terms** in Advanced lets you define canonical business terms,
  mistranscribed variants, required context, blocked context, case sensitivity,
  and notes. Applied rules are logged separately from raw ASR and filler/stutter
  filtering.
- **Vocabulary prompt** in Models uses the enabled glossary canonical terms as
  a Whisper-only recognition prompt.
- **Background recognition** can be enabled in `settings.json` with
  `"backgroundRecognitionEnabled": true`. It is experimental: Handy starts ASR
  during natural pauses, then uses that result as the final transcript prefix
  and only decodes the audio tail captured after the snapshot on hotkey release.

### CLI signals (forwarded to the running instance)

```
Handy.exe --toggle-transcription    # same as pressing the hotkey
Handy.exe --cancel                  # abort an in-flight recording
Handy.exe --show                    # bring up the settings window
```

A second launch with no signal does nothing (single-instance mutex).

### File-mode transcription (for debugging)

```
Handy.exe --transcribe-file path\to\clip.wav
```

Writes the transcript to `%APPDATA%\Handy\last-transcript.txt` and exits. Any
WAV is accepted (auto-converted to 16 kHz mono float). Bench tooling can pass
`--data-dir path\to\isolated\data` so file-mode runs do not mutate the user's
real settings.

To compare additive background recognition against the old full-buffer ASR wait:

```cmd
Handy.exe --bench-additive path\to\clip.wav --split 0.70
```

This writes `%APPDATA%\Handy\last-bench-additive.txt` and prints cold full-pass
time vs. prefix-pass and tail-pass timings. The tail pass approximates the
post-hotkey-release wait when the prefix pass completes during recording. See
[`docs/asr-latency-reductions.md`](docs/asr-latency-reductions.md) for the
current measurements and tradeoffs.

## Architecture

```
App.xaml.cs
 ├─ SingleInstance               named mutex + pipe for CLI forwarding
 ├─ LowLevelKeyHookService       WH_KEYBOARD_LL for rebindable hotkey + PTT
 ├─ AudioCaptureService          NAudio WaveInEvent, 16 kHz mono 16-bit
 ├─ ITranscriptionService        shared ASR contract selected by settings
 ├─ ParakeetTranscriptionService ONNX Runtime: preproc / encoder / decoder+joiner
 │    ├─ ParakeetTokenizer       vocab.txt + ▁-to-space + post-process regex
 ├─ WhisperTranscriptionService  Whisper.net / whisper.cpp GGML backend
 ├─ WhisperVocabularyPromptBuilder
 │                              glossary canonical terms → Whisper prompt
 ├─ TranscriptFilter             filler removal + stutter collapse
 ├─ DomainCorrectionService      context-aware domain glossary normalization
 ├─ ModelDownloadService         Parakeet tarballs + Whisper GGML downloads
 ├─ WavIo                        file-mode WAV loader with auto-conversion
 ├─ TextInjectionService         Clipboard + SendInput (CtrlV / ShiftInsert / CtrlShiftV / Direct)
 ├─ AudioFeedbackService         start/stop/cancel beeps (NAudio SignalGenerator)
 ├─ HistoryService               JSON-backed bounded transcript history
 ├─ AutostartService             HKCU Run key toggle
 ├─ AppSettings                  JSON, %APPDATA%\Handy\settings.json
 ├─ TrayIconManager              WinForms NotifyIcon hosted in WPF
 ├─ MainWindow (settings)        hotkey capture, mic picker, paste method, overlay, feedback
 └─ RecordingOverlay             click-through topmost WPF window (top/bottom/none)
```

All native interop lives in `PInvoke/NativeMethods.cs`.
Logs: `%APPDATA%\Handy\handy.log`. Settings: `%APPDATA%\Handy\settings.json`. History: `%APPDATA%\Handy\history.json`.

## Feature parity status

See [FEATURES.md](FEATURES.md). At a glance:

Implemented:
- Global hotkey with rebinding, toggle or push-to-talk.
- Cancel binding (Esc by default).
- Mic capture with device picker.
- Parakeet transcription (CPU).
- Paste methods: Ctrl+V, Direct, Ctrl+Shift+V, Shift+Insert, None.
- Paste delay + trailing-space + always-copy-to-clipboard options.
- Copy-last-transcript hotkey and tray action.
- Tray menu with Settings / History / Copy Last Transcript / Cancel / Exit.
- Recording overlay (top / bottom / none), click-through.
- Audio feedback beeps on start / stop / cancel.
- Autostart on login (HKCU Run key).
- Single-instance with `--toggle-transcription`, `--cancel`, `--show` forwarding.
- Transcript history persisted to JSON.
- VAD (Silero) trim before transcription.
- Whisper transcription via Whisper.net (tiny/base/small GGML).
- Context-aware domain glossary correction.
- Optional Whisper vocabulary prompt from glossary canonical terms.
- ASR vocabulary A/B bench in `bench/asr_vocab_ab.py`.
- Experimental background recognition during pauses for lower release-to-paste
  latency.
- Parallel-history WER bench in `bench/wer_bench.py`.

Deferred:
- Proper TDT duration-head consumption (matches transcribe-rs behaviour — vocab logits only).
- Post-process via OpenAI-compatible LLM.
- Theme-aware tray icons.

## Credits

The design, UX, model choices, and pipeline architecture are all
[upstream Handy](https://github.com/cjpais/Handy)'s. This port just
re-implements the same ideas in C# for a setup where we preferred a
.NET-based build. Please go star / support / use the upstream project
first.

- **Upstream:** <https://github.com/cjpais/Handy> — MIT, by
  [@cjpais](https://github.com/cjpais) and contributors.
- **Models:** NVIDIA NeMo Parakeet TDT (CC-BY-4.0), hosted by upstream at
  `https://blob.handy.computer/parakeet-v{2,3}-int8.tar.gz`.
- **Whisper backend:** [Whisper.net](https://github.com/sandrohanea/whisper.net)
  and [whisper.cpp](https://github.com/ggml-org/whisper.cpp).
- **VAD:** [snakers4/silero-vad](https://github.com/snakers4/silero-vad) (MIT).
- **ONNX Runtime:** Microsoft (MIT).
- **NAudio:** [Mark Heath](https://github.com/naudio/NAudio) (MIT).

## License

MIT — see [`LICENSE`](LICENSE). Upstream Handy is also MIT. If you
redistribute either, preserve both license notices.
