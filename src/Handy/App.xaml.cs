using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Handy.Services;

namespace Handy;

public partial class App : Application
{
    private TrayIconManager?         _tray;
    private LowLevelKeyHookService?  _hook;
    private AudioCaptureService?     _audio;
    private ITranscriptionService?     _asr;
    private SileroVadService?        _vad;
    private TextInjectionService?    _injector;
    private AudioFeedbackService?    _feedback;
    private HistoryService?          _history;
    private MainWindow?              _settingsWindow;
    private RecordingOverlay?        _overlay;
    private AppSettings              _settings = new();
    private string                   _dataDir = string.Empty;
    private string                   _parakeetModelDir = string.Empty;
    private string                   _activeBackend = "Parakeet";
    private string                   _activeWhisperModel = "base";

    private bool _recording;
    private bool _cancelled;
    private bool _transcribing;

    // Per-dictation diagnostics. Captured in StartRecording, finalised at the
    // end of StopAndTranscribe into a single greppable INFO line. The whole
    // chain (capture -> VAD -> ASR -> paste) is hard to triage without one
    // place to read what each stage did, so we emit one machine-parseable
    // summary alongside the existing per-stage log lines.
    private long _recStartTicks;

    // Speculative recognition (DCT-015). Detect natural pauses, run a
    // background ASR pass on the audio captured so far, and reuse the cached
    // text on hotkey release if the user trailed off in silence.
    //
    // _asrGate serialises ALL ASR calls (Parakeet & Whisper sessions are
    // non-reentrant) so a still-in-flight speculative pass blocks — not
    // collides with — a cold final pass.
    private readonly SemaphoreSlim _asrGate = new(1, 1);
    private CancellationTokenSource? _specCts;
    private long _lastNonSilentTicks;
    private long _lastSpecSnapshotSamples;
    private int _specInFlight;
    private int _specStartedCount;
    private int _specCompletedCount;
    private SpeculativeResult? _specCache;

    private sealed record SpeculativeResult(
        string Text,
        int SnapshotSamples,
        int VadOutSamples,
        long FinishedTicks);

    internal AppSettings Settings => _settings;
    internal ITranscriptionService? Asr => _asr;
    internal HistoryService? History => _history;

    internal sealed record ModelRuntimeStatus(
        string Backend,
        string ModelName,
        string Path,
        bool IsReady);

    private void OnStartup(object sender, StartupEventArgs e)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, ex) =>
            Log.Error($"Unhandled: {ex.ExceptionObject}");

        _dataDir = ResolveDataDir(e.Args);
        Directory.CreateDirectory(_dataDir);
        Log.Init(Path.Combine(_dataDir, "handy.log"));

        // --transcribe-file is a one-shot; skip the single-instance gate so it
        // doesn't conflict with a running tray instance.
        var isOneShot = Array.IndexOf(e.Args, "--transcribe-file") >= 0;
        if (!isOneShot && !SingleInstance.AcquireOrForward(e.Args))
        {
            Log.Info("Another Handy instance is already running; forwarded CLI and exiting.");
            Shutdown();
            return;
        }

        _settings = AppSettings.Load(_dataDir);
        AutostartService.Apply(_settings.Autostart);

        _parakeetModelDir = ResolveModelDir(_dataDir);

        // --transcribe-file <path>: skip the UI, transcribe the file, write
        // the result to %APPDATA%\Handy\last-transcript.txt, exit.
        var fileIndex = Array.IndexOf(e.Args, "--transcribe-file");
        if (fileIndex >= 0 && fileIndex + 1 < e.Args.Length)
        {
            RunTranscribeFile(e.Args[fileIndex + 1], _dataDir);
            Shutdown();
            return;
        }

        // CLI flags for the *local* instance (single-instance forwarding is in SingleInstance.cs).
        var startHiddenOverride = e.Args.Any(a => a == "--start-hidden");
        var noTray = e.Args.Any(a => a == "--no-tray");
        var showWindow = e.Args.Any(a => a == "--show");

        _asr = CreateTranscriptionService(_settings, _dataDir, _parakeetModelDir);
        _activeBackend = NormalizeBackend(_settings.TranscriptionBackend);
        _activeWhisperModel = WhisperTranscriptionService.NormalizeModelName(_settings.WhisperModel);
        _vad = new SileroVadService(Path.Combine(_dataDir, "models", "silero_vad.onnx"));
        _audio = new AudioCaptureService(_settings.MicrophoneDeviceName);
        _audio.OnLevels += levels => _overlay?.SetLevels(levels);
        _audio.OnLevels += OnAudioLevelsForSpeculative;
        _audio.Initialize(); // start continuous capture for pre-roll
        _injector = new TextInjectionService();
        _feedback = new AudioFeedbackService(_settings);
        _history = new HistoryService(_dataDir, _settings.HistoryLimit);

        _settingsWindow = new MainWindow();
        _settingsWindow.Icon = IconAssets.RenderHand(64, Color.FromRgb(0x58, 0x93, 0xDA));
        _settingsWindow.RefreshModelStatus();
        // Force HWND creation up-front so the process has a live top-level
        // window even in tray-only mode. Without this, Windows' foreground
        // eligibility rules filter SendInput deliveries into apps that run
        // in raw-input terminals (e.g. Claude Code in Windows Terminal).
        // Upstream Handy gets this for free via Tauri's always-alive webview.
        // Wrapped in try/catch because any throw here was taking down the
        // rest of OnStartup (including _hook.Install()) — regression seen
        // 2026-04-23 where Ctrl+Alt+Space stopped firing in all terminals.
        try { new WindowInteropHelper(_settingsWindow).EnsureHandle(); }
        catch (Exception ex) { Log.Warn($"EnsureHandle failed (non-fatal): {ex.Message}"); }
        _overlay = new RecordingOverlay();

        if (_settings.ShowTrayIcon && !noTray)
        {
            _tray = new TrayIconManager(
                onOpenSettings: () => ShowSettings(),
                onCopyLast:     () => CopyLastTranscript(),
                onOpenHistory:  () => ShowHistory(),
                onCancel:       () => OnCancel(),
                onQuit:         () => Shutdown());
        }

        _hook = new LowLevelKeyHookService();
        _hook.Configure(
            Hotkey.Parse(_settings.Hotkey),
            Hotkey.Parse(_settings.CancelHotkey),
            Hotkey.Parse(_settings.CopyLastHotkey));
        _hook.OnTrigger  += OnTrigger;
        _hook.OnCancel   += OnCancel;
        _hook.OnCopyLast += CopyLastTranscript;
        _hook.Install();

        // Listen for CLI-forwarded signals from the single-instance gate.
        SingleInstance.OnSignal += HandleSignal;

        Log.Info($"Handy started. Backend={_activeBackend} model={(_asr.IsReady ? DescribeActiveModelPath(_settings, _dataDir, _parakeetModelDir) : "NOT FOUND — see README")}");
        Log.Info($"PTT={_settings.PushToTalk} Paste={_settings.PasteMethod} Autostart={_settings.Autostart}");

        var shouldStartHidden = _settings.StartHidden || startHiddenOverride;
        var trayAvailable = _tray is not null;
        if (showWindow || !shouldStartHidden || !trayAvailable)
            ShowSettings();
    }

    private void OnExit(object sender, ExitEventArgs e)
    {
        try { _settings.Save(); } catch { /* best-effort */ }
        SingleInstance.OnSignal -= HandleSignal;
        SingleInstance.Shutdown();
        try { _specCts?.Cancel(); } catch { }
        try { _specCts?.Dispose(); } catch { }
        _hook?.Dispose();
        _audio?.Dispose();
        _asr?.Dispose();
        _vad?.Dispose();
        _feedback?.Dispose();
        _tray?.Dispose();
        try { _asrGate.Dispose(); } catch { }
        Log.Shutdown();
    }

    internal void ReloadSettings()
    {
        // Caller has already mutated _settings; just re-apply side effects.
        ReloadTranscriptionServiceIfNeeded();
        _hook?.Configure(
            Hotkey.Parse(_settings.Hotkey),
            Hotkey.Parse(_settings.CancelHotkey),
            Hotkey.Parse(_settings.CopyLastHotkey));
        AutostartService.Apply(_settings.Autostart);
        _feedback?.UpdateSettings(_settings);
        _audio?.SetPreferredDevice(_settings.MicrophoneDeviceName);
        _history?.SetLimit(_settings.HistoryLimit);
        _settings.Save();
    }

    private void ShowOverlay(RecordingOverlay.State state)
    {
        if (_overlay is null) return;
        if (string.Equals(_settings.OverlayPosition, "None", StringComparison.OrdinalIgnoreCase))
            return;
        _overlay.SetState(state);
        _overlay.Show();
        // Position after Show so ActualWidth is populated.
        Dispatcher.BeginInvoke(new Action(() => _overlay.ApplyPosition(_settings.OverlayPosition)));
    }

    private void UpdateOverlay(RecordingOverlay.State state)
    {
        if (_overlay is null || !_overlay.IsVisible) return;
        _overlay.SetState(state);
    }

    private void ShowSettings()
    {
        if (_settingsWindow is null) return;
        _settingsWindow.Show();
        if (_settingsWindow.WindowState == WindowState.Minimized)
            _settingsWindow.WindowState = WindowState.Normal;
        _settingsWindow.Topmost = true;   // pull to foreground
        _settingsWindow.Topmost = false;
        _settingsWindow.Activate();
    }

    private HistoryWindow? _historyWindow;
    private void ShowHistory()
    {
        if (_historyWindow is null || !_historyWindow.IsLoaded)
        {
            _historyWindow = new HistoryWindow();
        }
        _historyWindow.Show();
        _historyWindow.Activate();
    }

    private void OnTrigger(bool pressed)
    {
        if (_audio is null || _asr is null || _injector is null) return;

        if (_settings.PushToTalk)
        {
            if (pressed && !_recording) StartRecording();
            else if (!pressed && _recording) StopAndTranscribe();
            return;
        }

        // Toggle mode fires on press only; ignore release.
        if (!pressed) return;
        if (!_recording) StartRecording();
        else             StopAndTranscribe();
    }

    private void StartRecording()
    {
        if (_asr is null || !_asr.IsReady)
        {
            Log.Warn("Model not loaded; cannot start recording.");
            _tray?.Notify("Handy.NET", $"{NormalizeBackend(_settings.TranscriptionBackend)} model missing. See README.");
            return;
        }
        _recording = true;
        _cancelled = false;
        _recStartTicks = Stopwatch.GetTimestamp();
        ResetSpeculativeState();
        _tray?.SetState(Services.TrayIconManager.State.Recording);
        ShowOverlay(RecordingOverlay.State.Recording);
        _feedback?.PlayStart();
        _audio!.Start(_settings.PreRollMs);
        Log.Info($"Recording started (pre-roll {_settings.PreRollMs} ms).");
    }

    private void ResetSpeculativeState()
    {
        try { _specCts?.Cancel(); } catch { }
        try { _specCts?.Dispose(); } catch { }
        _specCts = new CancellationTokenSource();
        _lastNonSilentTicks = Stopwatch.GetTimestamp();
        _lastSpecSnapshotSamples = 0;
        Volatile.Write(ref _specInFlight, 0);
        _specStartedCount = 0;
        _specCompletedCount = 0;
        _specCache = null;
    }

    // Pause-driven trigger for the speculative ASR pass. Fires off the
    // capture thread (NAudio block callback ~50 ms), so the actual ASR work
    // is dispatched to Task.Run and gated by _asrGate so it can't collide
    // with the final pass.
    private void OnAudioLevelsForSpeculative(float[] levels)
    {
        if (!_settings.BackgroundRecognitionEnabled) return;
        if (!_recording) return;

        var now = Stopwatch.GetTimestamp();
        float maxBar = 0f;
        for (int i = 0; i < levels.Length; i++) if (levels[i] > maxBar) maxBar = levels[i];

        if (maxBar > _settings.BackgroundSilenceBarThreshold)
        {
            _lastNonSilentTicks = now;
            return;
        }

        var pauseMs = (now - _lastNonSilentTicks) * 1000 / Stopwatch.Frequency;
        if (pauseMs < _settings.BackgroundPauseTriggerMs) return;

        if (Interlocked.CompareExchange(ref _specInFlight, 1, 0) != 0) return;
        var cts = _specCts;
        if (cts is null || cts.IsCancellationRequested) { Volatile.Write(ref _specInFlight, 0); return; }
        var asr = _asr;
        if (asr is null || !asr.IsReady) { Volatile.Write(ref _specInFlight, 0); return; }

        // Hand off snapshot + ASR to a worker so we don't block the audio callback.
        var token = cts.Token;
        _ = Task.Run(() => RunSpeculativePass(asr, token), token);
    }

    private async Task RunSpeculativePass(ITranscriptionService asr, CancellationToken token)
    {
        try
        {
            if (_audio is null) return;
            var snapshot = _audio.SnapshotCurrentRecording();
            // Need at least minNewSpeechMs of additional audio since last snapshot
            // to justify spending another ASR cycle.
            var minNewSamples = _settings.BackgroundMinNewSpeechMs * 16;
            if (snapshot.Length - _lastSpecSnapshotSamples < minNewSamples) return;
            if (token.IsCancellationRequested) return;

            _specStartedCount++;
            _lastSpecSnapshotSamples = snapshot.Length;

            float[] forAsr = snapshot;
            if (_settings.VadEnabled && _vad is not null && _vad.IsReady)
                forAsr = _vad.Trim(snapshot, _settings.VadThreshold, _settings.VadPaddingMs);

            if (forAsr.Length < 16000 / 4) return; // sub-250 ms after VAD — skip

            // _asrGate serialises against the final pass; an in-flight final
            // pass will simply make this speculative pass wait then run, but
            // by then the final pass has already used the prior cache (or
            // not), so a late speculative pass just refreshes for the next
            // dictation slot, which is harmless.
            await _asrGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (token.IsCancellationRequested) return;
                var sw = Stopwatch.StartNew();
                var raw = await asr.TranscribeAsync(forAsr, CreateTranscriptionOptions(_settings))
                                   .ConfigureAwait(false);
                sw.Stop();
                if (token.IsCancellationRequested) return;
                var text = PostProcessTranscript(raw ?? string.Empty, _settings);
                _specCompletedCount++;
                _specCache = new SpeculativeResult(text, snapshot.Length, forAsr.Length, Stopwatch.GetTimestamp());
                Log.Info($"Spec: snapshotSamples={snapshot.Length} vadOutSamples={forAsr.Length} asrMs={sw.ElapsedMilliseconds} rawLen={(raw ?? string.Empty).Length} text=\"{text}\"");
            }
            finally
            {
                _asrGate.Release();
            }
        }
        catch (OperationCanceledException) { /* expected on cancel */ }
        catch (Exception ex)
        {
            Log.Warn($"Spec pass threw: {ex.Message}");
        }
        finally
        {
            Volatile.Write(ref _specInFlight, 0);
        }
    }

    private async void StopAndTranscribe()
    {
        _recording = false;
        var startTicks = _recStartTicks;
        var sw = Stopwatch.StartNew();

        var stopSw = Stopwatch.StartNew();
        var samples = await _audio!.StopAsync(_settings.PostRollMs);
        stopSw.Stop();
        var stopMs = stopSw.ElapsedMilliseconds;
        Log.Info($"Recording stopped. {samples.Length} samples captured (post-roll {_settings.PostRollMs} ms).");

        if (_cancelled)
        {
            SetUiState(isRecording: false, isTranscribing: false, hideOverlay: true);
            Log.Info("Recording cancelled; skipping transcription.");
            try { _specCts?.Cancel(); } catch { }
            _specCache = null;
            EmitDiag(startTicks, samples.Length, samples.Length, samples.Length, 0, 0, 0, "-", false, "cancelled",
                     stopMs, 0, 0, 0, 0, 0, sw.ElapsedMilliseconds,
                     _specStartedCount, _specCompletedCount, false, -1, 0);
            return;
        }
        if (samples.Length < 16000 / 4)
        {
            SetUiState(isRecording: false, isTranscribing: false, hideOverlay: true);
            Log.Warn("Recording too short; skipping.");
            try { _specCts?.Cancel(); } catch { }
            _specCache = null;
            EmitDiag(startTicks, samples.Length, samples.Length, samples.Length, 0, 0, 0, "-", false, "tooShort",
                     stopMs, 0, 0, 0, 0, 0, sw.ElapsedMilliseconds,
                     _specStartedCount, _specCompletedCount, false, -1, 0);
            return;
        }

        _feedback?.PlayStop();
        SetUiState(isRecording: false, isTranscribing: true, hideOverlay: false);
        _transcribing = true;
        int rawSampleCount = samples.Length;
        int vadOutCount = samples.Length;
        var rawCaptureSamples = samples;
        long asrMs = 0;
        int rawLen = 0, filtLen = 0;
        bool pasteOk = false;
        string pasteMethod = _settings.PasteMethod ?? "Direct";
        string outcome = "ok";
        long vadMs = 0;
        long postMs = 0;
        long historyMs = 0;
        long pasteMs = 0;
        long copyMs = 0;
        bool specCached = false;
        long specStaleMs = -1;
        int specVadDelta = 0;
        try
        {
            if (_settings.VadEnabled && _vad is not null && _vad.IsReady)
            {
                // Trim leading/trailing silence only — Smooth() was previously used here
                // but it removes mid-clip silence, severing transcription context across pauses.
                var vadSw = Stopwatch.StartNew();
                samples = _vad.Trim(samples, _settings.VadThreshold, _settings.VadPaddingMs);
                vadSw.Stop();
                vadMs = vadSw.ElapsedMilliseconds;
                vadOutCount = samples.Length;
            }

            if (_settings.SaveLastAudioForDiagnostics)
                SaveLastAudioDiagnostics(rawCaptureSamples, samples, _dataDir);

            var asr = _asr;
            if (asr is null || !asr.IsReady) throw new InvalidOperationException("Transcription model not loaded.");

            // Speculative cache reuse: if the user trailed off in silence,
            // the cached speculative result covers the same speech as the
            // final buffer and we can skip the cold ASR pass entirely.
            string? text = null;
            string? raw = null;
            var cache = _specCache;
            if (cache is not null)
            {
                specStaleMs = (Stopwatch.GetTimestamp() - cache.FinishedTicks) * 1000 / Stopwatch.Frequency;
                specVadDelta = samples.Length - cache.VadOutSamples;
                if (SpeculativeCachePolicy.ShouldReuseCache(samples.Length, cache.VadOutSamples, specStaleMs, _settings))
                {
                    text = cache.Text;
                    raw = cache.Text; // cached text is already post-processed; rawLen reflects that
                    rawLen = raw.Length;
                    asrMs = 0;
                    specCached = true;
                    Log.Info($"Spec cache HIT: text=\"{text}\" specStaleMs={specStaleMs} vadDelta={specVadDelta}");
                }
            }

            if (text is null)
            {
                await _asrGate.WaitAsync().ConfigureAwait(false);
                try
                {
                    // Re-check cache after the gate: an in-flight speculative
                    // pass may have completed while we waited, and would now
                    // satisfy the reuse rule we just failed.
                    cache = _specCache;
                    if (cache is not null)
                    {
                        var freshStaleMs = (Stopwatch.GetTimestamp() - cache.FinishedTicks) * 1000 / Stopwatch.Frequency;
                        if (SpeculativeCachePolicy.ShouldReuseCache(samples.Length, cache.VadOutSamples, freshStaleMs, _settings))
                        {
                            text = cache.Text;
                            raw = cache.Text;
                            rawLen = raw.Length;
                            asrMs = 0;
                            specCached = true;
                            specStaleMs = freshStaleMs;
                            specVadDelta = samples.Length - cache.VadOutSamples;
                            Log.Info($"Spec cache HIT (post-gate): text=\"{text}\" specStaleMs={specStaleMs} vadDelta={specVadDelta}");
                        }
                    }

                    if (text is null)
                    {
                        var asrSw = Stopwatch.StartNew();
                        raw = await asr.TranscribeAsync(samples, CreateTranscriptionOptions(_settings)).ConfigureAwait(false);
                        asrSw.Stop();
                        asrMs = asrSw.ElapsedMilliseconds;
                        rawLen = raw?.Length ?? 0;
                        Log.Info($"Raw: \"{raw}\"");
                        text = PostProcessTranscript(raw ?? string.Empty, _settings);
                    }
                }
                finally
                {
                    _asrGate.Release();
                }
            }

            var postSw = Stopwatch.StartNew();
            filtLen = text.Length;
            if (_settings.AppendTrailingSpace && !string.IsNullOrEmpty(text))
                text += ' ';
            postSw.Stop();
            postMs = postSw.ElapsedMilliseconds;
            Log.Info($"Transcript: {text}");

            if (!string.IsNullOrWhiteSpace(text))
            {
                Log.Info("Flow: before history.Add");
                var historySw = Stopwatch.StartNew();
                _history?.Add(text);
                historySw.Stop();
                historyMs = historySw.ElapsedMilliseconds;
                Log.Info("Flow: after history.Add, before paste");
                var pasteSw = Stopwatch.StartNew();
                try
                {
                    _injector!.Paste(text, _settings);
                    pasteOk = true;
                }
                catch (Exception pex)
                {
                    Log.Error($"Paste threw: {pex.Message}");
                    outcome = "pasteThrew";
                }
                finally
                {
                    pasteSw.Stop();
                    pasteMs = pasteSw.ElapsedMilliseconds;
                }
                var copySw = Stopwatch.StartNew();
                CopyTranscriptToClipboardIfEnabled(text);
                copySw.Stop();
                copyMs = copySw.ElapsedMilliseconds;
                Log.Info("Flow: after paste");
            }
            else
            {
                outcome = "emptyTranscript";
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Transcription failed: {ex}");
            _tray?.Notify("Handy.NET", "Transcription failed — see log.");
            outcome = "asrFailed";
        }
        finally
        {
            _transcribing = false;
            SetUiState(isRecording: false, isTranscribing: false, hideOverlay: true);
            // Cancel any in-flight speculative work so it doesn't write to
            // _specCache after this dictation ends and accidentally get reused
            // on the next one with stale audio.
            try { _specCts?.Cancel(); } catch { }
            _specCache = null;
            EmitDiag(startTicks, rawSampleCount, vadOutCount, samples.Length,
                     asrMs, rawLen, filtLen, pasteMethod, pasteOk, outcome,
                     stopMs, vadMs, postMs, historyMs, pasteMs, copyMs, sw.ElapsedMilliseconds,
                     _specStartedCount, _specCompletedCount, specCached, specStaleMs, specVadDelta);
        }
    }

    // One machine-greppable line per dictation. Pair with the existing per-stage
    // logs (Raw/Filter/Paste/Flow) when triaging "lost" dictations. Field order
    // is append-only so old parsers keep seeing the original fields first.
    private void EmitDiag(long recStartTicks, int rawSamples, int vadOutSamples, int finalSamples,
                          long asrMs, int rawLen, int filtLen,
                          string pasteMethod, bool pasteOk, string outcome,
                          long stopMs, long vadMs, long postMs, long historyMs,
                          long pasteMs, long copyMs, long totalMs,
                          int specCount, int specOk, bool specCached, long specStaleMs, int specVadDelta)
    {
        long heldMs = 0;
        if (recStartTicks > 0)
            heldMs = (Stopwatch.GetTimestamp() - recStartTicks) * 1000 / Stopwatch.Frequency;

        int audioMs = rawSamples * 1000 / 16000;
        int vadOutMs = vadOutSamples * 1000 / 16000;
        Log.Info($"Diag: heldMs={heldMs} audioMs={audioMs} samples={rawSamples} " +
                 $"vadOutSamples={vadOutSamples} vadOutMs={vadOutMs} finalSamples={finalSamples} " +
                 $"asrMs={asrMs} rawLen={rawLen} filtLen={filtLen} " +
                 $"paste={pasteMethod} pasteOk={(pasteOk ? "true" : "false")} outcome={outcome} " +
                 $"stopMs={stopMs} vadMs={vadMs} postMs={postMs} historyMs={historyMs} " +
                 $"pasteMs={pasteMs} copyMs={copyMs} totalMs={totalMs} " +
                 $"specCount={specCount} specOk={specOk} specCached={(specCached ? "true" : "false")} " +
                 $"specStaleMs={specStaleMs} specVadDelta={specVadDelta}");
    }

    private static void SaveLastAudioDiagnostics(float[] rawSamples, float[] asrSamples, string dataDir)
    {
        try
        {
            var dir = Path.Combine(dataDir, "diagnostics");
            var rawPath = Path.Combine(dir, "last-capture.raw.wav");
            var asrPath = Path.Combine(dir, "last-capture.asr.wav");
            WavIo.WriteMonoFloat16k(rawPath, rawSamples);
            WavIo.WriteMonoFloat16k(asrPath, asrSamples);
            Log.Info($"Audio diag: saved raw=\"{rawPath}\" asr=\"{asrPath}\" " +
                     $"raw[{DescribeAudioStats(rawSamples)}] asr[{DescribeAudioStats(asrSamples)}]");
        }
        catch (Exception ex)
        {
            Log.Warn($"Audio diag: save failed: {ex.Message}");
        }
    }

    private static string DescribeAudioStats(float[] samples)
    {
        if (samples.Length == 0) return "samples=0";

        double sumSq = 0;
        double peak = 0;
        int clipped = 0;
        int nonFinite = 0;

        foreach (var sample in samples)
        {
            if (!float.IsFinite(sample))
            {
                nonFinite++;
                continue;
            }
            var abs = Math.Abs(sample);
            if (abs > peak) peak = abs;
            sumSq += sample * sample;
            if (abs >= 0.98f) clipped++;
        }

        var valid = Math.Max(1, samples.Length - nonFinite);
        var rms = Math.Sqrt(sumSq / valid);
        return $"samples={samples.Length} rmsDb={Db(rms):F1} peakDb={Db(peak):F1} " +
               $"clippedPct={(clipped * 100.0 / samples.Length):F2} nonFinite={nonFinite}";
    }

    private static double Db(double amplitude) => amplitude > 1e-9 ? 20.0 * Math.Log10(amplitude) : -120.0;

    // UI touches (tray NotifyIcon + WPF overlay) must happen on the dispatcher.
    // Continuations after awaits in StopAndTranscribe can land on the threadpool,
    // so always marshal through here rather than touching _tray / _overlay directly.
    private void SetUiState(bool isRecording, bool isTranscribing, bool hideOverlay)
    {
        void Apply()
        {
            var trayState = isRecording ? Services.TrayIconManager.State.Recording
                          : isTranscribing ? Services.TrayIconManager.State.Transcribing
                          : Services.TrayIconManager.State.Idle;
            _tray?.SetState(trayState);

            if (hideOverlay)
                _overlay?.Hide();
            else if (isTranscribing)
                UpdateOverlay(RecordingOverlay.State.Transcribing);
        }

        if (Dispatcher.CheckAccess()) Apply();
        else Dispatcher.Invoke(Apply);
    }

    private void OnCancel()
    {
        if (!_recording) return;
        _cancelled = true;
        _feedback?.PlayCancel();
        // StopAndTranscribe will see _cancelled and return without ASR.
        StopAndTranscribe();
    }

    private void CopyLastTranscript()
    {
        var entry = _history?.LastEntry();
        if (entry is null || string.IsNullOrEmpty(entry.Text))
        {
            Log.Warn("copy-last-transcription: history empty or unreadable");
            _feedback?.PlayCancel();
            _tray?.Notify("Handy.NET", "No transcript yet.");
            return;
        }
        try
        {
            if (Dispatcher.CheckAccess()) Clipboard.SetText(entry.Text);
            else Dispatcher.Invoke(() => Clipboard.SetText(entry.Text));
            Log.Info($"copy-last-transcription: {entry.Text.Length} chars from {entry.TimestampUtc:O}");
        }
        catch (Exception ex)
        {
            Log.Warn($"copy-last-transcription: clipboard set failed: {ex.Message}");
            _tray?.Notify("Handy.NET", "Copy failed — see log.");
        }
    }

    private void CopyTranscriptToClipboardIfEnabled(string text)
    {
        if (!_settings.AlwaysCopyTranscriptToClipboard || string.IsNullOrEmpty(text))
            return;

        try
        {
            if (Dispatcher.CheckAccess()) Clipboard.SetText(text);
            else Dispatcher.Invoke(() => Clipboard.SetText(text));
            Log.Info($"always-copy-transcription: {text.Length} chars to clipboard");
        }
        catch (Exception ex)
        {
            Log.Warn($"always-copy-transcription: clipboard set failed: {ex.Message}");
        }
    }

    private void HandleSignal(string signal)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            switch (signal)
            {
                case "--toggle-transcription":
                    OnTrigger(pressed: true);
                    if (_settings.PushToTalk) OnTrigger(pressed: false); // synthetic toggle
                    break;
                case "--cancel":
                    OnCancel();
                    break;
                case "--show":
                    ShowSettings();
                    break;
            }
        }));
    }

    private void ReloadTranscriptionServiceIfNeeded()
    {
        var backend = NormalizeBackend(_settings.TranscriptionBackend);
        var whisperModel = WhisperTranscriptionService.NormalizeModelName(_settings.WhisperModel);
        _settings.TranscriptionBackend = backend;
        _settings.WhisperModel = whisperModel;

        if (string.Equals(backend, _activeBackend, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(whisperModel, _activeWhisperModel, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (_recording || _transcribing)
        {
            Log.Warn("Transcription backend changed while busy; saved setting will take effect after recording/transcription finishes and settings are applied again.");
            return;
        }

        var old = _asr;
        _asr = CreateTranscriptionService(_settings, _dataDir, _parakeetModelDir);
        _activeBackend = backend;
        _activeWhisperModel = whisperModel;
        old?.Dispose();
        _specCache = null; // any cache from the previous backend is no longer comparable
        _settingsWindow?.RefreshModelStatus();
        Log.Info($"Transcription backend switched to {_activeBackend} ({DescribeActiveModelPath(_settings, _dataDir, _parakeetModelDir)})");
    }

    internal ModelRuntimeStatus GetActiveModelStatus()
    {
        var backend = NormalizeBackend(_activeBackend);
        if (string.Equals(backend, "Whisper", StringComparison.OrdinalIgnoreCase))
        {
            var modelName = WhisperTranscriptionService.NormalizeModelName(_activeWhisperModel);
            var path = WhisperTranscriptionService.ResolveExistingModelPath(ResolveWhisperModelsDir(_dataDir), modelName);
            return new ModelRuntimeStatus("Whisper", $"Whisper {modelName}", path, _asr?.IsReady == true);
        }

        return new ModelRuntimeStatus(
            "Parakeet",
            DescribeParakeetModelName(_parakeetModelDir),
            _parakeetModelDir,
            _asr?.IsReady == true);
    }

    internal ModelRuntimeStatus DescribeModelSelection(string backend, string whisperModel)
    {
        backend = NormalizeBackend(backend);
        if (string.Equals(backend, "Whisper", StringComparison.OrdinalIgnoreCase))
        {
            var modelName = WhisperTranscriptionService.NormalizeModelName(whisperModel);
            var path = WhisperTranscriptionService.ResolveExistingModelPath(ResolveWhisperModelsDir(_dataDir), modelName);
            return new ModelRuntimeStatus("Whisper", $"Whisper {modelName}", path, File.Exists(path));
        }

        var parakeetModelDir = ResolveModelDir(_dataDir);
        return new ModelRuntimeStatus(
            "Parakeet",
            DescribeParakeetModelName(parakeetModelDir),
            parakeetModelDir,
            HasParakeetAssets(parakeetModelDir));
    }

    private static void RunTranscribeFile(string wavPath, string dataDir)
    {
        var outPath = Path.Combine(dataDir, "last-transcript.txt");
        var totalSw = Stopwatch.StartNew();
        try
        {
            if (!File.Exists(wavPath))
            {
                Log.Error($"--transcribe-file: WAV not found: {wavPath}");
                File.WriteAllText(outPath, $"ERROR: wav not found: {wavPath}");
                return;
            }

            var settings = AppSettings.Load(dataDir);
            var parakeetModelDir = ResolveModelDir(dataDir);
            var backend = NormalizeBackend(settings.TranscriptionBackend);
            var whisperModel = WhisperTranscriptionService.NormalizeModelName(settings.WhisperModel);
            var loadSw = Stopwatch.StartNew();
            using var asr = CreateTranscriptionService(settings, dataDir, parakeetModelDir);
            loadSw.Stop();
            if (!asr.IsReady)
            {
                Log.Error($"--transcribe-file: {backend} model not loaded.");
                File.WriteAllText(outPath, "ERROR: models not loaded");
                return;
            }

            var wavSw = Stopwatch.StartNew();
            var samples = WavIo.ReadMonoFloat16k(wavPath);
            wavSw.Stop();
            var rawSamples = samples.Length;
            Log.Info($"--transcribe-file: loaded {samples.Length} samples from {wavPath}");

            long vadMs = 0;
            using var vad = new SileroVadService(Path.Combine(dataDir, "models", "silero_vad.onnx"));
            if (vad.IsReady)
            {
                var vadSw = Stopwatch.StartNew();
                samples = vad.Trim(samples);
                vadSw.Stop();
                vadMs = vadSw.ElapsedMilliseconds;
            }

            var asrSw = Stopwatch.StartNew();
            var raw = asr.TranscribeAsync(samples, CreateTranscriptionOptions(settings)).GetAwaiter().GetResult();
            asrSw.Stop();
            var postSw = Stopwatch.StartNew();
            var text = PostProcessTranscript(raw ?? string.Empty, settings);
            postSw.Stop();
            Log.Info($"--transcribe-file: raw=\"{raw}\" postProcessed=\"{text}\"");
            var benchModel = backend == "Whisper"
                ? whisperModel
                : DescribeParakeetModelName(parakeetModelDir).Replace(' ', '_');
            Log.Info($"--transcribe-file: diag backend={backend} model={benchModel} " +
                     $"audioMs={rawSamples * 1000 / 16000} samples={rawSamples} vadOutSamples={samples.Length} vadOutMs={samples.Length * 1000 / 16000} " +
                     $"loadMs={loadSw.ElapsedMilliseconds} wavMs={wavSw.ElapsedMilliseconds} vadMs={vadMs} " +
                     $"asrMs={asrSw.ElapsedMilliseconds} postMs={postSw.ElapsedMilliseconds} totalMs={totalSw.ElapsedMilliseconds} " +
                     $"rawLen={(raw ?? string.Empty).Length} filtLen={text.Length}");
            File.WriteAllText(outPath, text);
        }
        catch (Exception ex)
        {
            Log.Error($"--transcribe-file failed: {ex}");
            File.WriteAllText(outPath, "ERROR: " + ex.Message);
        }
    }

    private static string ResolveModelDir(string dataDir)
    {
        var env = Environment.GetEnvironmentVariable("HANDY_MODEL_DIR");
        if (!string.IsNullOrWhiteSpace(env) && HasParakeetAssets(env))
            return env;

        var localModels = Path.Combine(dataDir, "models");
        Directory.CreateDirectory(localModels);
        foreach (var name in new[] { "parakeet-tdt-0.6b-v3-int8", "parakeet-tdt-0.6b-v2-int8" })
        {
            var candidate = Path.Combine(localModels, name);
            if (HasParakeetAssets(candidate)) return candidate;
        }

        var upstream = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "com.pais.handy", "models");
        foreach (var name in new[] { "parakeet-tdt-0.6b-v3-int8", "parakeet-tdt-0.6b-v2-int8" })
        {
            var candidate = Path.Combine(upstream, name);
            if (HasParakeetAssets(candidate)) return candidate;
        }

        return Path.Combine(localModels, "parakeet-tdt-0.6b-v2-int8");
    }

    private static string ResolveDataDir(string[] args)
    {
        var overrideDir = ArgumentValue(args, "--data-dir");
        if (!string.IsNullOrWhiteSpace(overrideDir))
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(overrideDir));

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Handy");
    }

    private static string PostProcessTranscript(string raw, AppSettings settings)
    {
        var filtered = Services.TranscriptFilter.Filter(raw ?? string.Empty, settings.AppLanguage, settings.CustomFillerWords);
        Log.Info(string.Equals(raw, filtered, StringComparison.Ordinal)
            ? $"Filter: (no change) lang={settings.AppLanguage}"
            : $"Filter: \"{raw}\" -> \"{filtered}\" (lang={settings.AppLanguage})");

        var glossary = Services.DomainCorrectionService.Apply(filtered, settings.DomainCorrections);
        if (glossary.Corrections.Count > 0)
        {
            var detail = string.Join("; ", glossary.Corrections.Select(c =>
                $"\"{c.From}\" -> \"{c.To}\" x{c.Count} ({c.Reason})"));
            Log.Info($"Glossary: \"{filtered}\" -> \"{glossary.Text}\" ({detail})");
        }
        else if (settings.DomainCorrections.Count > 0)
        {
            Log.Info($"Glossary: (no change) rules={settings.DomainCorrections.Count}");
        }

        return glossary.Text;
    }

    private static bool HasParakeetAssets(string dir) =>
        Directory.Exists(dir) &&
        File.Exists(Path.Combine(dir, "nemo128.onnx")) &&
        File.Exists(Path.Combine(dir, "encoder-model.int8.onnx")) &&
        File.Exists(Path.Combine(dir, "decoder_joint-model.int8.onnx")) &&
        File.Exists(Path.Combine(dir, "vocab.txt"));

    private static ITranscriptionService CreateTranscriptionService(AppSettings settings, string dataDir, string parakeetModelDir)
    {
        var backend = NormalizeBackend(settings.TranscriptionBackend);
        settings.TranscriptionBackend = backend;
        settings.WhisperModel = WhisperTranscriptionService.NormalizeModelName(settings.WhisperModel);

        if (string.Equals(backend, "Whisper", StringComparison.OrdinalIgnoreCase))
        {
            return new WhisperTranscriptionService(ResolveWhisperModelsDir(dataDir), settings.WhisperModel);
        }

        return new ParakeetTranscriptionService(parakeetModelDir);
    }

    private static string NormalizeBackend(string? backend)
    {
        return string.Equals(backend?.Trim(), "Whisper", StringComparison.OrdinalIgnoreCase)
            ? "Whisper"
            : "Parakeet";
    }

    private static string ResolveWhisperModelsDir(string dataDir)
    {
        var env = Environment.GetEnvironmentVariable("HANDY_WHISPER_MODEL_DIR");
        if (!string.IsNullOrWhiteSpace(env) && Directory.Exists(env))
            return env;

        return Path.Combine(dataDir, "models", "whisper");
    }

    private static TranscriptionOptions CreateTranscriptionOptions(AppSettings settings)
    {
        if (!settings.WhisperVocabularyPromptEnabled ||
            !string.Equals(NormalizeBackend(settings.TranscriptionBackend), "Whisper", StringComparison.OrdinalIgnoreCase))
        {
            return TranscriptionOptions.None;
        }

        var prompt = WhisperVocabularyPromptBuilder.Build(settings.DomainCorrections);
        return prompt.HasPrompt
            ? new TranscriptionOptions(prompt.Prompt, prompt.TermCount, settings.WhisperCarryInitialPrompt)
            : TranscriptionOptions.None;
    }

    private static string? ArgumentValue(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static string DescribeActiveModelPath(AppSettings settings, string dataDir, string parakeetModelDir)
    {
        if (string.Equals(NormalizeBackend(settings.TranscriptionBackend), "Whisper", StringComparison.OrdinalIgnoreCase))
        {
            return WhisperTranscriptionService.ResolveExistingModelPath(ResolveWhisperModelsDir(dataDir), settings.WhisperModel);
        }

        return parakeetModelDir;
    }

    private static string DescribeParakeetModelName(string path)
    {
        var dirName = Path.GetFileName(Path.TrimEndingDirectorySeparator(path)).ToLowerInvariant();
        if (dirName.Contains("v3")) return "Parakeet V3";
        if (dirName.Contains("v2")) return "Parakeet V2";
        return "Parakeet";
    }
}
