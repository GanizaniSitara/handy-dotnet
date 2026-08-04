# Handy.NET vs upstream Handy

Handy.NET is a .NET 8 / WPF reimplementation of [Handy](https://github.com/cjpais/Handy),
which is written in Rust + Tauri. This document records what each project has
that the other does not, so the trade-off in choosing one is explicit.

**Comparison drawn against upstream `b1b2d9f` (2026-08-03)** and Handy.NET
`v0.2.52`. Upstream moves quickly — 110 commits landed between the original port
point (`af6ec6c`, 2026-04-19) and this comparison. Re-check before relying on it.

Absence claims below were verified by searching upstream's `src-tauri/src/` at
that commit, and are stated as "no equivalent found" rather than "does not
exist" wherever a feature could plausibly be implemented under another name.

---

## Upstream has, Handy.NET does not

These are the reasons to prefer upstream.

| Feature | Notes |
|---|---|
| macOS and Linux | Handy.NET is Windows-only by design. The single biggest difference. |
| LLM post-processing | Configurable providers, API keys, prompt library, structured output. Handy.NET has no LLM layer at all. |
| GPU acceleration | Selectable transcription accelerator, ONNX Runtime execution provider, and GPU device index. Handy.NET is CPU-only. |
| Localised UI | Full app translation plus translate-to-English. Handy.NET is English-only, though the filler-word filter is language-aware. |
| Onboarding flow | Guided first-run setup with model download. Handy.NET drops the user straight into settings. |
| Update checks and release notes | Handy.NET has no updater; releases are downloaded manually. |
| Recording retention | Recordings stored and aged out on a retention policy. Handy.NET keeps transcripts only. |
| Themes and sound themes | Handy.NET has one light theme and fixed beeps. |
| Clipboard transaction paste | Delayed-rendering clipboard implementation behind `reliable_paste`, with clipboard image restore. Handy.NET uses a simpler set-and-chord approach. |
| Audio device breadth | Output device and clamshell microphone selection, always-on microphone, lazy stream close. |
| Mute while recording | Handy.NET does not mute other audio. |
| Alternative input backends | Selectable keyboard implementation, typing tool, and an external-script hook. |
| Overlay styles and visualiser | Handy.NET has a single overlay with level bars. |

## Handy.NET has, upstream does not

These are the reasons to prefer this fork.

| Feature | Notes |
|---|---|
| Paste target verification | Records the window that had focus when dictation began and refuses to type anywhere else, putting the transcript on the clipboard instead. No `GetForegroundWindow` or equivalent destination check was found anywhere in upstream's `src-tauri/src/`; upstream's paste work addresses clipboard mechanics, not destination identity. |
| Mid-injection focus guard | Character-by-character injection re-checks focus as it types, so a focus change part-way through cannot split one transcript across two applications. |
| Refused-input detection | A short injection count or `ERROR_ACCESS_DENIED` is reported rather than assumed successful, and an elevated (higher integrity level) target is named as the cause. |
| Crash and session records | A marker file plus phase tracking distinguishes a crash from a clean exit and reports which pipeline stage was reached. No equivalent found upstream, which has debug and log-level settings but no session lifecycle record. |
| Unfilterable lifecycle logging | Session and crash lines bypass the verbosity filter, so they cannot be silenced by a log-level setting. |
| Per-dictation diagnostic line | One machine-parseable line per dictation covering every stage timing and the outcome. |
| Speculative recognition | Decoding starts during natural pauses and splices a prefix with the tail on release, cutting perceived latency substantially on long dictations. No occurrences of "speculative" found upstream. |
| Domain Terms with context gates | Corrections carry variants plus require-any and block context gates, so ambiguous phrases are rewritten only in the right context. Upstream's `custom_words` with a similarity threshold is a different, simpler design. |
| Cancel during transcription | Escape discards a dictation at any point, including while decoding. |
| Copy-last-transcript hotkey | Recovers the previous transcript without opening the history window. No equivalent found upstream. |
| Stuck-trigger recovery | Polls real key state so a key-up swallowed by a UAC secure-desktop switch cannot leave the hotkey wedged. |
| In-app help | Help renders inside the app rather than depending on a file association. |
| Task capture | A second hotkey routes a dictation to a watched inbox folder instead of typing it. |

## Same feature, different behaviour

Worth knowing when comparing bug reports between the two.

| Area | Upstream | Handy.NET |
|---|---|---|
| Default paste method | Platform-dependent; `direct` was removed from the macOS UI | `Direct`, because terminals and TUIs intercept Ctrl+V |
| Vocabulary handling | `custom_words` matched against a similarity threshold | Explicit correction rules with context gates |
| Transcription engine | Whisper and Parakeet with GPU execution providers | Parakeet TDT int8 by default, Whisper available, CPU only |
| Data directory | `%APPDATA%\com.pais.handy` | `%APPDATA%\Handy`, reusing upstream's model cache |
| Log retention | Single rotation generation | Five generations, dated lines |

---

## Deliberately not ported

Recorded during the original port and re-checked at this comparison. These
remain out of scope rather than pending: LLM post-processing, GPU execution
providers, macOS and Linux support, the signed updater, and UI localisation.
Each is a substantial subsystem whose value does not survive the fork's narrower
goal — a dependency-light Windows dictation tool that builds with nothing but
the .NET SDK.

Platform-specific upstream mechanisms have direct equivalents here rather than
ports: Unix `SIGUSR1`/`SIGUSR2` external triggers correspond to the
`--toggle-transcription` and `--cancel` CLI flags.
