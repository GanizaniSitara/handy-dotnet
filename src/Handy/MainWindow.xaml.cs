using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Handy.Services;

namespace Handy;

public partial class MainWindow : Window
{
    private const int WM_SYSCOMMAND = 0x0112;
    private const int SC_MINIMIZE   = 0xF020;

    private AppSettings? _settings;
    private bool _loading;
    private readonly DisplayLogBuffer _displayLog = new();
    private readonly System.Windows.Threading.DispatcherTimer _logTimer;
    private ObservableCollection<DomainCorrection> _domainCorrections = new();

    public MainWindow()
    {
        InitializeComponent();
        LogoPath.Data = Geometry.Parse(IconAssets.HandPathData);
        var versionLabel = ResolveDisplayVersion();
        VersionText.Text = string.IsNullOrEmpty(versionLabel) ? "" : $"v{versionLabel}";
        if (!string.IsNullOrEmpty(versionLabel))
            Title = $"Handy.NET — Settings (v{versionLabel})";
        Log.Sink = AppendLine;
        _logTimer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(250),
        };
        _logTimer.Tick += (_, _) => RenderLog();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) { RenderLog(); _logTimer.Start(); }
            else _logTimer.Stop();
        };

        Loaded += (_, _) => LoadFromSettings();

        // Intercept the system Minimize command BEFORE WPF turns it into
        // WindowState.Minimized. Hiding directly avoids the awkward
        // Hide/Show/ShowInTaskbar interaction that leaves the window
        // un-restorable on some Windows 10/11 configurations.
        SourceInitialized += (_, _) =>
        {
            var src = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            src?.AddHook(WndProc);
        };
    }

    // InformationalVersion carries the full git-derived stamp (commit count +
    // short SHA + dirty flag) injected by the build target in Handy.csproj.
    // Fall back to the plain AssemblyVersion if the attribute is missing
    // (e.g. when running outside a git checkout).
    internal static string ResolveDisplayVersion()
    {
        var asm = typeof(MainWindow).Assembly;
        var info = asm.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(info)) return info;
        var v = asm.GetName().Version;
        return v is null ? string.Empty : $"{v.Major}.{v.Minor}.{v.Build}";
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_SYSCOMMAND && (wParam.ToInt32() & 0xFFF0) == SC_MINIMIZE)
        {
            Hide();
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void RefreshModelStatus()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(RefreshModelStatus));
            return;
        }

        if (ActiveModelSummaryText is null) return;

        var app = (App)Application.Current;
        var active = app.GetActiveModelStatus();
        var selectedBackend = ComboContent(TranscriptionBackendCombo, _settings?.TranscriptionBackend ?? active.Backend);
        var selectedWhisperModel = ComboContent(WhisperModelCombo, _settings?.WhisperModel ?? "base");
        var selectedParakeetVariant = ComboContent(ParakeetVariantCombo, _settings?.ParakeetVariant ?? "Auto");
        var selectedProcessor = ComboContent(ProcessorCombo, App.NormalizeExecutionProvider(_settings?.ParakeetExecutionProvider));
        var pending = app.DescribeModelSelection(selectedBackend, selectedWhisperModel, selectedParakeetVariant, selectedProcessor);
        var gpuModelMissing = selectedProcessor == "GPU" && !app.HasGpuModel(selectedParakeetVariant);

        ActiveModelSummaryText.Text = $"{active.ModelName} ({active.Backend})";
        ActiveModelStatusText.Text = app.IsModelLoading ? "Loading - dictation will be available shortly" : active.IsReady
            ? "Ready - currently used for transcription"
            : "Unavailable - check the log for missing models or loading errors";
        ActiveModelStatusText.Foreground = app.IsModelLoading ? Brushes.DarkOrange : active.IsReady ? Brushes.ForestGreen : Brushes.Firebrick;
        ActiveModelPathText.Text = active.Path;

        var selectionMatchesActive =
            string.Equals(active.Backend, pending.Backend, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(active.ModelName, pending.ModelName, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(active.Path, pending.Path, StringComparison.OrdinalIgnoreCase);

        PendingModelSummaryText.Text = $"{pending.ModelName} ({pending.Backend})";
        if (_gpuDownloadRunning) { /* progress text owns the status line */ }
        else if (gpuModelMissing && !string.Equals(pending.Backend, "Whisper", StringComparison.OrdinalIgnoreCase))
        {
            PendingModelStatusText.Text = "GPU model not downloaded - click Apply to download it (about 2.5 GB)";
            PendingModelStatusText.Foreground = Brushes.DarkOrange;
        }
        else
        {
            PendingModelStatusText.Text = pending.IsReady
                ? selectionMatchesActive
                    ? "Already active"
                    : "Ready - click Apply to switch"
                : "Missing - download this model before applying";
            PendingModelStatusText.Foreground = pending.IsReady
                ? selectionMatchesActive ? Brushes.ForestGreen : Brushes.DarkOrange
                : Brushes.Firebrick;
        }
        PendingModelPathText.Text = pending.Path;

        var whisperSelected = string.Equals(pending.Backend, "Whisper", StringComparison.OrdinalIgnoreCase);
        WhisperModelCombo.IsEnabled = whisperSelected;
        WhisperModelCombo.Opacity = whisperSelected ? 1.0 : 0.55;
        WhisperPromptCheck.IsEnabled = whisperSelected;
        WhisperPromptCheck.Opacity = whisperSelected ? 1.0 : 0.55;
        WhisperCarryPromptCheck.IsEnabled = whisperSelected;
        WhisperCarryPromptCheck.Opacity = whisperSelected ? 1.0 : 0.55;
        ParakeetVariantCombo.IsEnabled = !whisperSelected;
        ParakeetVariantCombo.Opacity = whisperSelected ? 0.55 : 1.0;
        ProcessorCombo.IsEnabled = !whisperSelected && !_gpuDownloadRunning;
        ProcessorCombo.Opacity = whisperSelected ? 0.55 : 1.0;
        if (ProcessorHint is not null)
        {
            ProcessorHint.Text = whisperSelected
                ? "CPU or GPU applies to Parakeet. Whisper always runs on the CPU here."
                : "GPU is several times faster on long dictations and frees the CPU. It works on any DirectX 12 graphics card with nothing to install. If the card is unavailable, Handy.NET falls back to the CPU on its own; the Status line above says where it is really running.";
        }

        // The biasing control was already greyed out for non-Whisper backends,
        // but nothing said why, and nothing said that Domain Terms still work
        // regardless. Greying a box without a reason reads as a bug.
        if (BiasingAvailabilityHint is not null)
        {
            BiasingAvailabilityHint.Text = whisperSelected
                ? "Uses your Domain Terms as a vocabulary prompt so Whisper is more likely to hear them correctly. Your Domain Terms are also applied as corrections after transcription, on every backend."
                : "Recognition biasing needs the Whisper backend — Parakeet has no equivalent. Your Domain Terms still work: they are applied as corrections after transcription, on every backend.";
        }

        // Speed is the reason to stay on Parakeet, and it isn't visible at the
        // point where the backend is actually chosen. Figures are the DCT-015
        // bench medians on jfk.wav; they are indicative, not a live measurement.
        if (BackendCostHint is not null)
        {
            BackendCostHint.Text = whisperSelected
                ? "Whisper is slower than Parakeet: roughly 1.5 s for tiny.en and 3 s for base, against about 0.7 s for Parakeet on a short clip. Pick it when you need recognition biasing."
                : "Parakeet is the fastest option here — roughly 0.7 s on a short clip, against about 1.5 s for Whisper tiny.en and 3 s for Whisper base.";
        }
    }

    private void LoadFromSettings()
    {
        _loading = true;
        try
        {
            _settings = ((App)Application.Current).Settings;

            HotkeyBox.Text   = _settings.Hotkey;
            TaskCaptureHotkeyBox.Text = _settings.TaskCaptureHotkey;
            TaskCaptureInboxBox.Text = _settings.TaskCaptureInbox;
            CancelBox.Text   = _settings.CancelHotkey;
            CancelChordBox.Text = _settings.CancelChordHotkey;
            CancelChordCheck.IsChecked = _settings.CancelChordEnabled;
            CopyLastBox.Text = _settings.CopyLastHotkey;
            RetypeLastBox.Text = _settings.RetypeLastHotkey;
            PttCheck.IsChecked = _settings.PushToTalk;

            SelectComboByContent(PasteMethodCombo, _settings.PasteMethod);
            TrailingSpaceCheck.IsChecked = _settings.AppendTrailingSpace;
            SelectComboByContent(ClipboardCombo,  _settings.ClipboardHandling);
            AlwaysCopyTranscriptCheck.IsChecked = _settings.AlwaysCopyTranscriptToClipboard;
            SelectComboByContent(PasteFocusPolicyCombo, _settings.PasteFocusPolicy);
            BackgroundRecognitionCheck.IsChecked = _settings.BackgroundRecognitionEnabled;
            SelectComboByContent(AutoSubmitCombo, _settings.AutoSubmitKey);
            _domainCorrections = new ObservableCollection<DomainCorrection>(
                (_settings.DomainCorrections ?? new List<DomainCorrection>()).Select(c => c.Clone()));
            DomainCorrectionsGrid.ItemsSource = _domainCorrections;

            PasteDelayBox.Text      = _settings.PasteDelayMs.ToString();
            DirectCharDelayBox.Text = _settings.DirectCharDelayMs.ToString();
            DirectCharDelayCitrixBox.Text = _settings.DirectCharDelayMsCitrix.ToString();
            SelectComboByContent(LogDisplayVerbosityCombo, _settings.LogDisplayVerbosity);
            SelectComboByContent(LogFileVerbosityCombo,    _settings.LogFileVerbosity);
            PreRollBox.Text         = _settings.PreRollMs.ToString();
            PostRollBox.Text        = _settings.PostRollMs.ToString();

            MicCombo.Items.Clear();
            MicCombo.Items.Add(new ComboBoxItem { Content = "(system default)" });
            foreach (var name in AudioCaptureService.EnumerateInputDevices())
                MicCombo.Items.Add(new ComboBoxItem { Content = name });
            SelectMic(_settings.MicrophoneDeviceName);

            SelectComboByContent(OverlayCombo, _settings.OverlayPosition);

            BeepStartCheck.IsChecked   = _settings.BeepOnStart;
            BeepStopCheck.IsChecked    = _settings.BeepOnStop;
            BeepCancelCheck.IsChecked  = _settings.BeepOnCancel;
            BeepVolumeSlider.Value     = Math.Clamp(_settings.BeepVolume, 0, 1);
            BeepVolumePct.Text         = $"{(int)(BeepVolumeSlider.Value * 100)}%";

            VadCheck.IsChecked         = _settings.VadEnabled;
            VadThresholdBox.Text       = _settings.VadThreshold.ToString("F2", CultureInfo.InvariantCulture);
            VadPaddingBox.Text         = _settings.VadPaddingMs.ToString();
            VadMaxSilenceMsBox.Text    = _settings.VadMaxSilenceMs.ToString();

            AutostartCheck.IsChecked   = _settings.Autostart;
            StartHiddenCheck.IsChecked = _settings.StartHidden;
            ShowTrayCheck.IsChecked    = _settings.ShowTrayIcon;

            HistoryLimitBox.Text = _settings.HistoryLimit.ToString();

            SelectComboByContent(TranscriptionBackendCombo, _settings.TranscriptionBackend);
            SelectComboByContent(ParakeetVariantCombo, App.NormalizeParakeetVariant(_settings.ParakeetVariant));
            SelectComboByContent(ProcessorCombo, App.NormalizeExecutionProvider(_settings.ParakeetExecutionProvider));
            SelectComboByContent(WhisperModelCombo, WhisperTranscriptionService.NormalizeModelName(_settings.WhisperModel));
            WhisperPromptCheck.IsChecked = _settings.WhisperVocabularyPromptEnabled;
            WhisperCarryPromptCheck.IsChecked = _settings.WhisperCarryInitialPrompt;
            SelectComboByContent(DownloadWhisperModelCombo, WhisperTranscriptionService.NormalizeModelName(_settings.WhisperModel));

            // The two halves of this feature live on different pages: the table
            // is here on Advanced, the biasing switch is on Models. Without a
            // pointer, finding the second half is pure luck.
            DomainTermsScopeHint.Text =
                "These corrections are applied after transcription and work with every backend. " +
                "Whisper can additionally use them to bias recognition itself — see Recognition biasing on the Models tab.";

            RefreshModelStatus();
        }
        finally { _loading = false; }
    }

    private static void SelectComboByContent(ComboBox combo, string value)
    {
        foreach (ComboBoxItem item in combo.Items)
        {
            if (string.Equals((string?)item.Content, value, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedItem = item;
                return;
            }
        }
        if (combo.Items.Count > 0) combo.SelectedIndex = 0;
    }

    private void SelectMic(string preferred)
    {
        if (string.IsNullOrEmpty(preferred))
        {
            MicCombo.SelectedIndex = 0;
            return;
        }
        foreach (ComboBoxItem item in MicCombo.Items)
        {
            if ((string?)item.Content == preferred)
            {
                MicCombo.SelectedItem = item;
                return;
            }
        }
        MicCombo.SelectedIndex = 0;
    }

    private void OnApply(object sender, RoutedEventArgs e)
    {
        if (_settings is null || _loading) return;

        _settings.Hotkey         = string.IsNullOrWhiteSpace(HotkeyBox.Text)   ? _settings.Hotkey         : HotkeyBox.Text;
        _settings.TaskCaptureHotkey = string.IsNullOrWhiteSpace(TaskCaptureHotkeyBox.Text)
            ? _settings.TaskCaptureHotkey
            : TaskCaptureHotkeyBox.Text;
        _settings.TaskCaptureInbox = string.IsNullOrWhiteSpace(TaskCaptureInboxBox.Text)
            ? _settings.TaskCaptureInbox
            : TaskCaptureInboxBox.Text.Trim();
        _settings.CancelHotkey   = string.IsNullOrWhiteSpace(CancelBox.Text)   ? _settings.CancelHotkey   : CancelBox.Text;
        _settings.CancelChordHotkey = string.IsNullOrWhiteSpace(CancelChordBox.Text) ? _settings.CancelChordHotkey : CancelChordBox.Text;
        _settings.CancelChordEnabled = CancelChordCheck.IsChecked == true;
        _settings.CopyLastHotkey = string.IsNullOrWhiteSpace(CopyLastBox.Text) ? _settings.CopyLastHotkey : CopyLastBox.Text;
        _settings.RetypeLastHotkey = string.IsNullOrWhiteSpace(RetypeLastBox.Text) ? _settings.RetypeLastHotkey : RetypeLastBox.Text;
        _settings.PushToTalk     = PttCheck.IsChecked == true;

        _settings.PasteMethod         = (string?)((ComboBoxItem)PasteMethodCombo.SelectedItem)?.Content ?? "CtrlV";
        _settings.AppendTrailingSpace = TrailingSpaceCheck.IsChecked == true;
        _settings.ClipboardHandling   = (string?)((ComboBoxItem)ClipboardCombo.SelectedItem)?.Content  ?? "DontModify";
        _settings.AlwaysCopyTranscriptToClipboard = AlwaysCopyTranscriptCheck.IsChecked == true;
        _settings.PasteFocusPolicy    = (string?)((ComboBoxItem)PasteFocusPolicyCombo.SelectedItem)?.Content ?? "RestoreAndPaste";
        _settings.BackgroundRecognitionEnabled = BackgroundRecognitionCheck.IsChecked == true;
        _settings.AutoSubmitKey       = (string?)((ComboBoxItem)AutoSubmitCombo.SelectedItem)?.Content ?? "None";
        DomainCorrectionsGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        DomainCorrectionsGrid.CommitEdit(DataGridEditingUnit.Row, true);
        _settings.DomainCorrections = _domainCorrections
            .Where(IsUsableDomainCorrection)
            .Select(c =>
            {
                var copy = c.Clone();
                copy.NormalizeForSave();
                return copy;
            })
            .ToList();

        if (int.TryParse(PasteDelayBox.Text,      out var ms)    && ms    is >= 0 and <= 2000) _settings.PasteDelayMs      = ms;
        if (int.TryParse(DirectCharDelayBox.Text, out var dc)    && dc    is >= 0 and <= 100)  _settings.DirectCharDelayMs = dc;
        if (int.TryParse(DirectCharDelayCitrixBox.Text, out var dcc) && dcc is >= 0 and <= 100) _settings.DirectCharDelayMsCitrix = dcc;
        _settings.LogDisplayVerbosity = ComboContent(LogDisplayVerbosityCombo, _settings.LogDisplayVerbosity);
        _settings.LogFileVerbosity    = ComboContent(LogFileVerbosityCombo,    _settings.LogFileVerbosity);
        if (int.TryParse(PreRollBox.Text,         out var pre)   && pre   is >= 0 and <= 2000) _settings.PreRollMs         = pre;
        if (int.TryParse(PostRollBox.Text,        out var post)  && post  is >= 0 and <= 2000) _settings.PostRollMs        = post;

        var mic = (string?)((ComboBoxItem)MicCombo.SelectedItem)?.Content ?? "";
        _settings.MicrophoneDeviceName = mic.StartsWith("(system") ? string.Empty : mic;

        _settings.OverlayPosition = (string?)((ComboBoxItem)OverlayCombo.SelectedItem)?.Content ?? "None";

        _settings.BeepOnStart  = BeepStartCheck.IsChecked  == true;
        _settings.BeepOnStop   = BeepStopCheck.IsChecked   == true;
        _settings.BeepOnCancel = BeepCancelCheck.IsChecked == true;
        _settings.BeepVolume   = BeepVolumeSlider.Value;

        _settings.VadEnabled = VadCheck.IsChecked == true;
        if (double.TryParse(VadThresholdBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var th) && th >= 0 && th <= 1)
            _settings.VadThreshold = th;
        if (int.TryParse(VadPaddingBox.Text, out var pad) && pad is >= 0 and <= 2000)
            _settings.VadPaddingMs = pad;
        if (int.TryParse(VadMaxSilenceMsBox.Text, out var gap) && gap is >= 500 and <= 120_000)
            _settings.VadMaxSilenceMs = gap;

        _settings.Autostart    = AutostartCheck.IsChecked == true;
        _settings.StartHidden  = StartHiddenCheck.IsChecked == true;
        _settings.ShowTrayIcon = ShowTrayCheck.IsChecked == true;

        if (int.TryParse(HistoryLimitBox.Text, out var hl) && hl is >= 1 and <= 1000)
            _settings.HistoryLimit = hl;

        _settings.TranscriptionBackend = (string?)((ComboBoxItem)TranscriptionBackendCombo.SelectedItem)?.Content ?? "Parakeet";
        _settings.ParakeetVariant = App.NormalizeParakeetVariant(
            (string?)((ComboBoxItem)ParakeetVariantCombo.SelectedItem)?.Content);
        _settings.WhisperModel = (string?)((ComboBoxItem)WhisperModelCombo.SelectedItem)?.Content ?? "base";
        _settings.WhisperVocabularyPromptEnabled = WhisperPromptCheck.IsChecked == true;
        _settings.WhisperCarryInitialPrompt = WhisperCarryPromptCheck.IsChecked == true;
        _settings.ParakeetExecutionProvider = App.NormalizeExecutionProvider(ComboContent(ProcessorCombo, "CPU"));

        var app = (App)Application.Current;
        app.ReloadSettings();
        // GPU chosen but its model is not on disk yet: keep dictating on the
        // CPU model, fetch the GPU one in the background, then switch.
        if (_settings.ParakeetExecutionProvider == "GPU" && !_gpuDownloadRunning &&
            !string.Equals(_settings.TranscriptionBackend?.Trim(), "Whisper", StringComparison.OrdinalIgnoreCase) &&
            !app.HasGpuModel(_settings.ParakeetVariant))
            _ = DownloadGpuModelThenSwitchAsync(app);
        RefreshModelStatus();
        Log.Info("Settings applied.");
        ShowSaveStatus($"Saved at {DateTime.Now:HH:mm:ss}");
    }

    private System.Windows.Threading.DispatcherTimer? _saveStatusTimer;

    private void ShowSaveStatus(string message)
    {
        if (SaveStatusText is null) return;
        SaveStatusText.Text = message;
        _saveStatusTimer?.Stop();
        _saveStatusTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(4),
        };
        _saveStatusTimer.Tick += (_, _) =>
        {
            SaveStatusText.Text = string.Empty;
            _saveStatusTimer?.Stop();
        };
        _saveStatusTimer.Start();
    }

    private void OnBeepVolumeChanged(object sender, System.Windows.RoutedPropertyChangedEventArgs<double> e)
    {
        if (BeepVolumePct is not null) BeepVolumePct.Text = $"{(int)(e.NewValue * 100)}%";
    }

    private static bool IsUsableDomainCorrection(DomainCorrection? correction) =>
        correction is not null &&
        !string.IsNullOrWhiteSpace(correction.To) &&
        correction.EffectiveVariants().Count > 0;

    private void OnModelSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        RefreshModelStatus();
    }

    // Verbosity drop-downs apply live — no need to click Apply just to filter
    // the log view. The persisted setting still only changes on Apply, but
    // Log.SetVerbosity is updated immediately so the panel reflects the change.
    private void OnLogVerbosityChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        var display = Log.ParseVerbosity(ComboContent(LogDisplayVerbosityCombo, _settings?.LogDisplayVerbosity ?? "Normal"),
                                         LogVerbosity.Normal);
        var file    = Log.ParseVerbosity(ComboContent(LogFileVerbosityCombo,    _settings?.LogFileVerbosity    ?? "Debug"),
                                         LogVerbosity.Debug);
        Log.SetVerbosity(file, display);
    }

    private void OnHotkeyBoxKeyDown(object sender, KeyEventArgs e)   => CaptureChord(HotkeyBox, e);
    private void OnTaskCaptureHotkeyBoxKeyDown(object sender, KeyEventArgs e)
        => CaptureChord(TaskCaptureHotkeyBox, e);
    private void OnCancelBoxKeyDown(object sender, KeyEventArgs e)   => CaptureChord(CancelBox, e, singleKey: true);
    private void OnCancelChordBoxKeyDown(object sender, KeyEventArgs e) => CaptureChord(CancelChordBox, e);
    private void OnCopyLastBoxKeyDown(object sender, KeyEventArgs e) => CaptureChord(CopyLastBox, e);
    private void OnRetypeLastBoxKeyDown(object sender, KeyEventArgs e) => CaptureChord(RetypeLastBox, e);

    private static void CaptureChord(TextBox box, KeyEventArgs e, bool singleKey = false)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl
                or Key.LeftAlt or Key.RightAlt
                or Key.LeftShift or Key.RightShift
                or Key.LWin or Key.RWin)
            return;

        var parts = new System.Collections.Generic.List<string>();
        if (!singleKey)
        {
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))     parts.Add("Alt");
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))   parts.Add("Shift");
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        }
        parts.Add(KeyToName(key));
        box.Text = string.Join("+", parts);
    }

    private static string KeyToName(Key key) => key switch
    {
        Key.Space => "Space",
        Key.Enter => "Enter",
        Key.Escape => "Escape",
        Key.Tab => "Tab",
        Key.Back => "Backspace",
        Key.Delete => "Delete",
        Key.Insert => "Insert",
        Key.Home => "Home",
        Key.End => "End",
        Key.PageUp => "PageUp",
        Key.PageDown => "PageDown",
        Key.Up => "Up",
        Key.Down => "Down",
        Key.Left => "Left",
        Key.Right => "Right",
        Key.CapsLock => "CapsLock",
        Key.NumLock => "NumLock",
        Key.Scroll => "ScrollLock",
        Key.PrintScreen => "PrintScreen",
        Key.Pause => "Pause",
        _ when key >= Key.F1 && key <= Key.F12 => key.ToString(),
        _ when key >= Key.A  && key <= Key.Z   => key.ToString(),
        _ when key >= Key.D0 && key <= Key.D9  => key.ToString().Substring(1),
        _ => key.ToString(),
    };

    private void AppendLine(string line)
        => _displayLog.Append(line);

    private void RenderLog()
    {
        if (!IsVisible || PanelLog.Visibility != Visibility.Visible) return;
        var text = _displayLog.TakeChangedText();
        if (text is null) return;
        LogBox.Text = text;
        LogBox.ScrollToEnd();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
    }

    private void OnHide(object sender, RoutedEventArgs e) => Hide();

    private void OnNavChecked(object sender, RoutedEventArgs e)
    {
        if (PanelGeneral is null) return;
        PanelGeneral.Visibility  = NavGeneral.IsChecked  == true ? Visibility.Visible : Visibility.Collapsed;
        PanelAdvanced.Visibility = NavAdvanced.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PanelModels.Visibility   = NavModels.IsChecked   == true ? Visibility.Visible : Visibility.Collapsed;
        PanelLog.Visibility      = NavLog.IsChecked      == true ? Visibility.Visible : Visibility.Collapsed;
        RenderLog();
    }

    private bool _gpuDownloadRunning;

    private async System.Threading.Tasks.Task DownloadGpuModelThenSwitchAsync(App app)
    {
        _gpuDownloadRunning = true;
        var variant = app.GpuDownloadVariant(_settings.ParakeetVariant);
        var progress = new Progress<ModelDownloadService.Progress>(p =>
        {
            var mb = p.TotalBytes.HasValue
                ? $"{p.BytesSoFar / (1024.0 * 1024.0):F0} / {p.TotalBytes.Value / (1024.0 * 1024.0):F0} MB"
                : $"{p.BytesSoFar / (1024.0 * 1024.0):F0} MB";
            PendingModelStatusText.Text = $"{p.Phase}: {mb}. Dictation keeps working on the CPU meanwhile.";
            PendingModelStatusText.Foreground = Brushes.DarkOrange;
        });
        try
        {
            await ModelDownloadService.DownloadGpuAsync(app.ModelsRoot, variant, progress,
                System.Threading.CancellationToken.None);
            _gpuDownloadRunning = false;
            // Same settings, but the GPU directory now resolves: this reloads
            // the model onto the GPU.
            app.ReloadSettings();
            PendingModelStatusText.Text = "GPU model installed - switching now";
            PendingModelStatusText.Foreground = Brushes.ForestGreen;
        }
        catch (Exception ex)
        {
            _gpuDownloadRunning = false;
            PendingModelStatusText.Text = $"GPU model download failed: {ex.Message}. Click Apply to retry.";
            PendingModelStatusText.Foreground = Brushes.Firebrick;
            Log.Error($"GPU model download failed: {ex}");
            return;
        }
        RefreshModelStatus();
    }

    private async void OnDownloadModel(object sender, RoutedEventArgs e)
    {
        var pick = (string?)((ComboBoxItem)DownloadVariantCombo.SelectedItem)?.Content ?? "V2";
        var variant = pick == "V3" ? ModelDownloadService.Variant.V3 : ModelDownloadService.Variant.V2;

        var dataDir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Handy");
        var modelsRoot = System.IO.Path.Combine(dataDir, "models");

        DownloadButton.IsEnabled = false;
        DownloadStatus.Text = $"Starting {pick} download…";

        var progress = new Progress<ModelDownloadService.Progress>(p =>
        {
            var pct = (p.Fraction * 100).ToString("F1");
            var mb  = p.TotalBytes.HasValue
                ? $"{p.BytesSoFar / (1024.0 * 1024.0):F1} / {p.TotalBytes.Value / (1024.0 * 1024.0):F1} MB"
                : $"{p.BytesSoFar / (1024.0 * 1024.0):F1} MB";
            DownloadStatus.Text = $"{p.Phase}: {pct}%  ({mb})";
        });

        try
        {
            var dir = await ModelDownloadService.DownloadAsync(modelsRoot, variant, progress,
                System.Threading.CancellationToken.None);
            DownloadStatus.Text = $"Installed to {dir}. Restart Handy.NET to use it.";
            RefreshModelStatus();
        }
        catch (Exception ex)
        {
            DownloadStatus.Text = $"Download failed: {ex.Message}";
            Log.Error($"Model download failed: {ex}");
        }
        finally
        {
            DownloadButton.IsEnabled = true;
        }
    }

    private async void OnDownloadWhisperModel(object sender, RoutedEventArgs e)
    {
        var model = (string?)((ComboBoxItem)DownloadWhisperModelCombo.SelectedItem)?.Content ?? "base";

        var dataDir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Handy");
        var whisperDir = System.IO.Path.Combine(dataDir, "models", "whisper");

        DownloadWhisperButton.IsEnabled = false;
        DownloadWhisperStatus.Text = $"Starting {model} download...";

        var progress = new Progress<ModelDownloadService.Progress>(p =>
        {
            var mb = p.TotalBytes.HasValue && p.TotalBytes.Value > 0
                ? $"{p.BytesSoFar / (1024.0 * 1024.0):F1} / {p.TotalBytes.Value / (1024.0 * 1024.0):F1} MB"
                : $"{p.BytesSoFar / (1024.0 * 1024.0):F1} MB";
            DownloadWhisperStatus.Text = $"{p.Phase}: {mb}";
        });

        try
        {
            var path = await System.Threading.Tasks.Task.Run(async () =>
                await ModelDownloadService.DownloadWhisperAsync(whisperDir, model, progress,
                    System.Threading.CancellationToken.None));
            DownloadWhisperStatus.Text = $"Installed to {path}";
            RefreshModelStatus();
        }
        catch (Exception ex)
        {
            DownloadWhisperStatus.Text = $"Download failed: {ex.Message}";
            Log.Error($"Whisper model download failed: {ex}");
        }
        finally
        {
            DownloadWhisperButton.IsEnabled = true;
        }
    }

    private void OnOpenHistory(object sender, RoutedEventArgs e)
    {
        var w = new HistoryWindow();
        w.Show();
    }

    private void OnOpenHelp(object sender, RoutedEventArgs e)
    {
        if (HelpWindow.TryShow(this, "help.md", "Help")) return;

        MessageBox.Show(
            "Help file was not found. Expected docs\\help.md next to Handy.exe or in the repository docs folder.",
            "Handy.NET",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void OnOpenDomainTermsHelp(object sender, RoutedEventArgs e)
    {
        // Rendered in-app rather than shelled out. Process.Start with
        // UseShellExecute needs a registered .md handler, and a stock Windows
        // install has none — so this button used to produce an error dialog
        // instead of the help, for every user, not just unlucky ones.
        if (HelpWindow.TryShow(this, "domain-terms.md", "Domain Terms")) return;

        MessageBox.Show(
            "Domain Terms help file was not found. Expected docs\\domain-terms.md next to Handy.exe or in the repository docs folder.",
            "Handy.NET",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private static string ComboContent(ComboBox combo, string fallback)
        => (string?)((ComboBoxItem?)combo.SelectedItem)?.Content ?? fallback;
}
