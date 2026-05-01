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
Settings file, history, and on-disk Parakeet models are cross-compatible.

**Upstream stack:** Rust + Tauri + WebView2 + React frontend + `transcribe-rs`.
**This port:** .NET 8 WPF + NAudio + Microsoft.ML.OnnxRuntime.

## Why port it

The port keeps the upstream feature set while building on the standard
.NET toolchain:

- `.NET 8 SDK` as the only prerequisite to build.
- Runtime is the in-box `.NET 8 Desktop Runtime` — or publish self-contained
  and drop the folder.

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
`WhisperModel` to `"tiny"`, `"base"`, or `"small"`; models live under
`%APPDATA%\Handy\models\whisper\` as `ggml-{model}.bin`.

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
%APPDATA%\Handy\models\whisper\ggml-base.bin
%APPDATA%\Handy\models\whisper\ggml-small.bin
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
WAV is accepted (auto-converted to 16 kHz mono float).

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
- Paste delay + trailing-space option.
- Tray menu with Settings / Copy Last Transcript / Cancel / Quit.
- Recording overlay (top / bottom / none), click-through.
- Audio feedback beeps on start / stop / cancel.
- Autostart on login (HKCU Run key).
- Single-instance with `--toggle-transcription`, `--cancel`, `--show` forwarding.
- Transcript history persisted to JSON.
- VAD (Silero) trim before transcription.
- Whisper transcription via Whisper.net (tiny/base/small GGML).

Deferred:
- Proper TDT duration-head consumption (matches transcribe-rs behaviour — vocab logits only).
- Post-process via OpenAI-compatible LLM.
- Rich history UI.
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
