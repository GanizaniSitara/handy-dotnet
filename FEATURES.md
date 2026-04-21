# Handy feature surface — port status

Tracking parity between upstream [Handy](https://github.com/cjpais/Handy)
(Rust + Tauri) and this .NET port. Legend: ✅ implemented · 🔸 partial ·
⏳ deferred · ❌ out of scope.

## Deliberately not ported (with reasons)

These upstream features are explicitly *not* planned for this port. Each has a
concrete reason — either it fights the toolchain constraints the port was built
to honour, or the value/cost ratio doesn't justify the work.

| Feature | Status | Why not |
|---|---|---|
| Whisper GGML backend | ❌ | Upstream supports both Whisper and Parakeet; the port picks one high-quality backend (Parakeet) and owns it end-to-end. Re-adding Whisper would double the model/runtime surface for marginal user gain. |
| Post-processing via LLM (OpenAI / Ollama / Apple Intelligence) | ❌ | Large feature surface (provider plumbing, prompt management, structured output). Out of scope for a dictation port. |
| Apple Intelligence on-device LLM | ❌ | macOS-only, not applicable to a Windows .NET port. |
| Chinese script conversion (`ferrous_opencc`) | ❌ | Niche, script-specific; Parakeet V3 handles multilingual natively. |
| GPU / Metal / Vulkan / CUDA accelerator selection | ❌ | ONNX Runtime CPU is already ~13× realtime. Adding execution-provider plumbing (NVIDIA/DirectML) is a multi-day job for sub-second savings. |
| Linux / macOS platform paths (xdotool, wtype, dotool, NSPanel) | ❌ | Windows port. |
| Unix SIGUSR1/SIGUSR2 external triggers | ❌ | Windows doesn't have SIGUSR. Equivalent here is the `--toggle-transcription`/`--cancel` CLI flags, already implemented. |
| Raycast integration | ❌ | macOS/Raycast-only. |
| Signed updater (minisign / Tauri updater) | ❌ | No signing infra; corporate builds self-deploy. |
| Clamshell / laptop lid detection for device routing | ❌ | Upstream uses macOS-specific APIs. |
| Custom vocabulary (`custom_words`) / word-correction threshold | ❌ | Parakeet has no hot-word API; would need a post-decode string replacer. |
| Translate-to-English flag | ❌ | Parakeet has no translate head (Whisper does). |

## Held for later (tracked, not blocked)

| Feature | Why held |
|---|---|
| Theme-aware tray icons (light/dark × idle/recording) | Needs artwork (4 icon PNGs). Trivial code, zero art. |
| Mute-while-recording other apps | Requires `IAudioSessionManager2` COM interop, a few hundred lines of unmanaged plumbing for a minor polish feature. |
| Unload-model / Model-select / Check-updates tray items | Model-select needs a settings refresh round-trip and UI affordance; low user impact vs. the settings screen that already covers it. |
| Rich history entry pinning | Basic history is live (persist / copy / delete / clear). Pinning ("saved entries") is a secondary UX layer. |
| Debug panel (Ctrl+Shift+D) | Settings screen already shows the live log; a dedicated panel adds little. |
| Portable mode (redirect data dir next to exe) | Niche; %APPDATA% works for ~99% of installs. |
| Sound-theme packs | Synthesised beeps cover the use case; custom WAV packs are cosmetic. |
| Mute-while-recording | COM-heavy; see above. |

## Core pipeline

- ✅ Global hotkey triggers a recording session — toggle or push-to-talk, rebindable.
- ✅ Microphone capture via NAudio with device selection.
- 🔸 Mute-while-recording — not implemented (upstream mutes other apps during capture).
- ✅ Pre-roll / post-roll audio buffer — continuous ring keeps the last 3 s of mic audio; recording includes the configured ms before the hotkey press and after the release.
- ✅ VAD (Silero v5 ONNX) trims leading/trailing silence pre-transcription. Configurable threshold + padding.
- ✅ Local transcription via **Parakeet V2/V3 int8** (NeMo Conformer TDT) through ONNX Runtime.
- ✅ Full TDT decoding — vocab head + duration head, greedy frame-skipping per predicted duration.
- 🔸 Whisper path — upstream also supports Whisper GGML; this port dropped it in favour of one high-quality backend.
- ❌ GPU/accelerator selection (Metal / Vulkan / CUDA) — CPU only.
- ✅ In-app Parakeet V2/V3 model download from `blob.handy.computer`, tar.gz extracted via `System.Formats.Tar`.
- ✅ Auto-discover upstream's Parakeet cache at `%APPDATA%\com.pais.handy\models\`.
- ❌ Post-processing via OpenAI-compatible LLM (Ollama/OpenAI/Apple Intelligence).
- ✅ Text injection — `CtrlV`, `Direct`, `CtrlShiftV`, `ShiftInsert`, `None`.
- ✅ Configurable paste delay and trailing-space append.
- ✅ Clipboard handling — `DontModify` restores the user's clipboard contents after paste; `CopyToClipboard` leaves the transcript there.
- ✅ Auto-submit key after paste — `None` / `Enter` / `CtrlEnter`.
- ❌ Word-correction threshold + custom vocabulary (`custom_words`).
- ❌ Translate-to-English flag (Parakeet does not expose a translate head).
- ❌ Selected language override (Parakeet V3 is multilingual with auto-detect; V2 is English).

## UI / shell

- ✅ System tray icon.
- ⏳ Theme-aware tray icon (light/dark swap).
- ✅ Tray menu — Settings, Copy Last Transcript, Cancel, Quit. Status line shows recording state.
- ⏳ Unload Model / Model Select / Check Updates menu items.
- ✅ Recording overlay window — top / bottom / none, click-through, always-on-top.
- ✅ Settings window — hotkey capture, PTT, paste method + delay, mic picker, overlay position, autostart, start-hidden, beeps toggle, trailing space, **in-app Parakeet V2/V3 download**.
- ✅ Transcription history — persisted to `%APPDATA%\Handy\history.json`, with full browser UI (copy/delete/clear) reachable from tray *History…*.
- ✅ Audio feedback sounds — synthesised start/stop/cancel tones via NAudio SignalGenerator.
- ⏳ Sound-theme packs (upstream bundles multiple WAV sets).
- ⏳ Debug mode panel (Ctrl+Shift+D) with verbose logs and audio dump.

## Platform / lifecycle

- ✅ Single-instance enforcement — named mutex + named pipe for CLI forwarding.
- ✅ CLI flags — `--toggle-transcription`, `--cancel`, `--show`, `--start-hidden`, `--no-tray`, `--transcribe-file <path>`.
- ✅ Autostart at login via HKCU Run key.
- ✅ Start-hidden (tray-only) launch.
- ⏳ Portable mode (redirect data dir next to binary).
- ✅ File logging with rotation at 500 KB to `handy.log.1`; cross-process writes serialised via named mutex.
- ❌ Opt-in signed updater (minisign/Tauri updater).
- ❌ Unix SIGUSR1/SIGUSR2 handlers — Windows only.
- ❌ Linux-specific paste fallbacks (xdotool / wtype / dotool) — Windows only.
- ❌ macOS paste fallbacks / Apple Intelligence path — Windows only.

## Settings surface

- ✅ Persistent JSON-backed settings store at `%APPDATA%\Handy\settings.json`.
- ✅ Per-binding shortcut rebinding via UI capture.
- ❌ Clamshell/laptop detection for device routing.
- ❌ Raycast integration.

## Files written at runtime

```
%APPDATA%\Handy\
├── settings.json       user config (JSON, round-trips unknown keys)
├── handy.log           flat log (no rotation yet)
├── history.json        transcript history
├── last-transcript.txt only for --transcribe-file mode
└── models\             drop Parakeet model dirs here (or reuse upstream's cache)
```
