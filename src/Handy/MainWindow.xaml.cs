using System;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using Handy.Services;

namespace Handy;

public partial class MainWindow : Window
{
    private const int WM_SYSCOMMAND = 0x0112;
    private const int SC_MINIMIZE   = 0xF020;

    private AppSettings? _settings;
    private bool _loading;

    public MainWindow()
    {
        InitializeComponent();
        Log.Sink = AppendLine;

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

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_SYSCOMMAND && (wParam.ToInt32() & 0xFFF0) == SC_MINIMIZE)
        {
            Hide();
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void SetModelPath(string path) => ModelPathText.Text = path;

    private void LoadFromSettings()
    {
        _loading = true;
        try
        {
            _settings = ((App)Application.Current).Settings;

            HotkeyBox.Text  = _settings.Hotkey;
            CancelBox.Text  = _settings.CancelHotkey;
            PttCheck.IsChecked = _settings.PushToTalk;

            SelectComboByContent(PasteMethodCombo, _settings.PasteMethod);
            TrailingSpaceCheck.IsChecked = _settings.AppendTrailingSpace;
            SelectComboByContent(ClipboardCombo,  _settings.ClipboardHandling);
            SelectComboByContent(AutoSubmitCombo, _settings.AutoSubmitKey);

            PasteDelayBox.Text      = _settings.PasteDelayMs.ToString();
            DirectCharDelayBox.Text = _settings.DirectCharDelayMs.ToString();
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

            VadCheck.IsChecked      = _settings.VadEnabled;
            VadThresholdBox.Text    = _settings.VadThreshold.ToString("F2", CultureInfo.InvariantCulture);
            VadPaddingBox.Text      = _settings.VadPaddingMs.ToString();

            AutostartCheck.IsChecked   = _settings.Autostart;
            StartHiddenCheck.IsChecked = _settings.StartHidden;
            ShowTrayCheck.IsChecked    = _settings.ShowTrayIcon;

            HistoryLimitBox.Text = _settings.HistoryLimit.ToString();
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

        _settings.Hotkey       = string.IsNullOrWhiteSpace(HotkeyBox.Text) ? _settings.Hotkey : HotkeyBox.Text;
        _settings.CancelHotkey = string.IsNullOrWhiteSpace(CancelBox.Text) ? _settings.CancelHotkey : CancelBox.Text;
        _settings.PushToTalk   = PttCheck.IsChecked == true;

        _settings.PasteMethod         = (string?)((ComboBoxItem)PasteMethodCombo.SelectedItem)?.Content ?? "CtrlV";
        _settings.AppendTrailingSpace = TrailingSpaceCheck.IsChecked == true;
        _settings.ClipboardHandling   = (string?)((ComboBoxItem)ClipboardCombo.SelectedItem)?.Content  ?? "DontModify";
        _settings.AutoSubmitKey       = (string?)((ComboBoxItem)AutoSubmitCombo.SelectedItem)?.Content ?? "None";

        if (int.TryParse(PasteDelayBox.Text,      out var ms)    && ms    is >= 0 and <= 2000) _settings.PasteDelayMs      = ms;
        if (int.TryParse(DirectCharDelayBox.Text, out var dc)    && dc    is >= 0 and <= 100)  _settings.DirectCharDelayMs = dc;
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

        _settings.Autostart    = AutostartCheck.IsChecked == true;
        _settings.StartHidden  = StartHiddenCheck.IsChecked == true;
        _settings.ShowTrayIcon = ShowTrayCheck.IsChecked == true;

        if (int.TryParse(HistoryLimitBox.Text, out var hl) && hl is >= 1 and <= 1000)
            _settings.HistoryLimit = hl;

        ((App)Application.Current).ReloadSettings();
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

    private void OnHotkeyBoxKeyDown(object sender, KeyEventArgs e) => CaptureChord(HotkeyBox, e);
    private void OnCancelBoxKeyDown(object sender, KeyEventArgs e) => CaptureChord(CancelBox, e, singleKey: true);

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
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => AppendLine(line)));
            return;
        }
        LogBox.AppendText(line + Environment.NewLine);
        LogBox.ScrollToEnd();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
    }

    private void OnHide(object sender, RoutedEventArgs e) => Hide();
    private void OnQuit(object sender, RoutedEventArgs e) => Application.Current.Shutdown();

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
            DownloadStatus.Text = $"Installed to {dir}. Restart Handy to use it.";
            ModelPathText.Text = dir;
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

    private void OnOpenHistory(object sender, RoutedEventArgs e)
    {
        var w = new HistoryWindow();
        w.Show();
    }
}
