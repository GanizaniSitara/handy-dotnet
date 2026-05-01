using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Interop;
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

    internal AppSettings Settings => _settings;
    internal ITranscriptionService? Asr => _asr;
    internal HistoryService? History => _history;

    private void OnStartup(object sender, StartupEventArgs e)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, ex) =>
            Log.Error($"Unhandled: {ex.ExceptionObject}");

        _dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Handy");
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
        _audio.Initialize(); // start continuous capture for pre-roll
        _injector = new TextInjectionService();
        _feedback = new AudioFeedbackService(_settings);
        _history = new HistoryService(_dataDir, _settings.HistoryLimit);

        _settingsWindow = new MainWindow();
        _settingsWindow.SetModelPath(DescribeActiveModelPath(_settings, _dataDir, _parakeetModelDir));
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
        _hook.Configure(Hotkey.Parse(_settings.Hotkey), Hotkey.Parse(_settings.CancelHotkey));
        _hook.OnTrigger += OnTrigger;
        _hook.OnCancel  += OnCancel;
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
        _hook?.Dispose();
        _audio?.Dispose();
        _asr?.Dispose();
        _vad?.Dispose();
        _feedback?.Dispose();
        _tray?.Dispose();
        Log.Shutdown();
    }

    internal void ReloadSettings()
    {
        // Caller has already mutated _settings; just re-apply side effects.
        ReloadTranscriptionServiceIfNeeded();
        _hook?.Configure(Hotkey.Parse(_settings.Hotkey), Hotkey.Parse(_settings.CancelHotkey));
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
            _tray?.Notify("Handy", $"{NormalizeBackend(_settings.TranscriptionBackend)} model missing. See README.");
            return;
        }
        _recording = true;
        _cancelled = false;
        _tray?.SetState(Services.TrayIconManager.State.Recording);
        ShowOverlay(RecordingOverlay.State.Recording);
        _feedback?.PlayStart();
        _audio!.Start(_settings.PreRollMs);
        Log.Info($"Recording started (pre-roll {_settings.PreRollMs} ms).");
    }

    private async void StopAndTranscribe()
    {
        _recording = false;
        var samples = await _audio!.StopAsync(_settings.PostRollMs);
        Log.Info($"Recording stopped. {samples.Length} samples captured (post-roll {_settings.PostRollMs} ms).");

        if (_cancelled)
        {
            SetUiState(isRecording: false, isTranscribing: false, hideOverlay: true);
            Log.Info("Recording cancelled; skipping transcription.");
            return;
        }
        if (samples.Length < 16000 / 4)
        {
            SetUiState(isRecording: false, isTranscribing: false, hideOverlay: true);
            Log.Warn("Recording too short; skipping.");
            return;
        }

        _feedback?.PlayStop();
        SetUiState(isRecording: false, isTranscribing: true, hideOverlay: false);
        _transcribing = true;
        try
        {
            if (_settings.VadEnabled && _vad is not null && _vad.IsReady)
            {
                // Trim leading/trailing silence only — Smooth() was previously used here
                // but it removes mid-clip silence, severing transcription context across pauses.
                samples = _vad.Trim(samples, _settings.VadThreshold, _settings.VadPaddingMs);
            }

            var asr = _asr;
            if (asr is null || !asr.IsReady) throw new InvalidOperationException("Transcription model not loaded.");

            var raw = await asr.TranscribeAsync(samples);
            Log.Info($"Raw: \"{raw}\"");
            var text = Services.TranscriptFilter.Filter(raw, _settings.AppLanguage, _settings.CustomFillerWords);
            Log.Info(raw == text
                ? $"Filter: (no change) lang={_settings.AppLanguage}"
                : $"Filter: \"{raw}\" -> \"{text}\" (lang={_settings.AppLanguage})");
            if (_settings.AppendTrailingSpace && !string.IsNullOrEmpty(text))
                text += ' ';
            Log.Info($"Transcript: {text}");

            if (!string.IsNullOrWhiteSpace(text))
            {
                Log.Info("Flow: before history.Add");
                _history?.Add(text);
                Log.Info("Flow: after history.Add, before paste");
                _injector!.Paste(text, _settings);
                Log.Info("Flow: after paste");
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Transcription failed: {ex}");
            _tray?.Notify("Handy", "Transcription failed — see log.");
        }
        finally
        {
            _transcribing = false;
            SetUiState(isRecording: false, isTranscribing: false, hideOverlay: true);
        }
    }

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
        var last = _history?.Last();
        if (string.IsNullOrEmpty(last))
        {
            _tray?.Notify("Handy", "No transcript yet.");
            return;
        }
        try
        {
            if (Dispatcher.CheckAccess()) Clipboard.SetText(last);
            else Dispatcher.Invoke(() => Clipboard.SetText(last));
        }
        catch (Exception ex)
        {
            Log.Warn($"Copy last: clipboard set failed: {ex.Message}");
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
        _settingsWindow?.SetModelPath(DescribeActiveModelPath(_settings, _dataDir, _parakeetModelDir));
        Log.Info($"Transcription backend switched to {_activeBackend} ({DescribeActiveModelPath(_settings, _dataDir, _parakeetModelDir)})");
    }

    private static void RunTranscribeFile(string wavPath, string dataDir)
    {
        var outPath = Path.Combine(dataDir, "last-transcript.txt");
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
            using var asr = CreateTranscriptionService(settings, dataDir, parakeetModelDir);
            if (!asr.IsReady)
            {
                Log.Error($"--transcribe-file: {NormalizeBackend(settings.TranscriptionBackend)} model not loaded.");
                File.WriteAllText(outPath, "ERROR: models not loaded");
                return;
            }

            var samples = WavIo.ReadMonoFloat16k(wavPath);
            Log.Info($"--transcribe-file: loaded {samples.Length} samples from {wavPath}");

            using var vad = new SileroVadService(Path.Combine(dataDir, "models", "silero_vad.onnx"));
            if (vad.IsReady)
                samples = vad.Trim(samples);

            var raw = asr.TranscribeAsync(samples).GetAwaiter().GetResult();
            var text = Services.TranscriptFilter.Filter(raw ?? string.Empty, settings.AppLanguage, settings.CustomFillerWords);
            Log.Info($"--transcribe-file: raw=\"{raw}\" filtered=\"{text}\"");
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
        return Path.Combine(dataDir, "models", "whisper");
    }

    private static string DescribeActiveModelPath(AppSettings settings, string dataDir, string parakeetModelDir)
    {
        if (string.Equals(NormalizeBackend(settings.TranscriptionBackend), "Whisper", StringComparison.OrdinalIgnoreCase))
        {
            return WhisperTranscriptionService.ModelPathFor(ResolveWhisperModelsDir(dataDir), settings.WhisperModel);
        }

        return parakeetModelDir;
    }
}
