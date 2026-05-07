using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Handy.Services;

/// <summary>
/// Persistent user settings. Mirrors the subset of upstream Handy settings
/// that the .NET port actually honours; additional keys round-trip untouched.
/// </summary>
public sealed class AppSettings
{
    private const int CurrentSettingsVersion = 2;

    public int SettingsVersion { get; set; } = CurrentSettingsVersion;

    public string Hotkey { get; set; } = "Ctrl+Alt+Space";
    public string CancelHotkey { get; set; } = "Escape";

    /// <summary>Recovery hotkey: copies the most recent transcription back onto the clipboard
    /// without triggering paste. Useful when the auto-paste failed silently (e.g. terminal
    /// integrity-level mismatch) and the user wants to paste it manually somewhere else.</summary>
    public string CopyLastHotkey { get; set; } = "Ctrl+Alt+Shift+Space";

    /// <summary>Push-to-talk (hold) vs. toggle.</summary>
    public bool PushToTalk { get; set; } = false;

    /// <summary>Ctrl+V | Direct | None | ShiftInsert | CtrlShiftV. Default matches
    /// upstream Handy: Direct (Unicode keystroke injection) works in both GUI apps
    /// and shells/TUIs (which often eat Ctrl+V as quoted-insert).</summary>
    public string PasteMethod { get; set; } = "Direct";

    public int PasteDelayMs { get; set; } = 50;

    /// <summary>Per-character sleep (ms) for Direct paste mode. 0 = max speed (native desktops).
    /// Citrix/VDI sessions need a few ms to stop keystrokes being dropped or reordered.</summary>
    public int DirectCharDelayMs { get; set; } = 0;

    public bool AppendTrailingSpace { get; set; } = false;

    /// <summary>DontModify | CopyToClipboard — upstream ClipboardHandling.</summary>
    public string ClipboardHandling { get; set; } = "DontModify";

    /// <summary>None | Enter | CtrlEnter.</summary>
    public string AutoSubmitKey { get; set; } = "None";

    /// <summary>Empty = default input device.</summary>
    public string MicrophoneDeviceName { get; set; } = string.Empty;

    public bool Autostart { get; set; } = false;
    public bool StartHidden { get; set; } = true;
    public bool ShowTrayIcon { get; set; } = true;

    /// <summary>None | Top | Bottom. Default matches upstream Handy: Bottom.</summary>
    public string OverlayPosition { get; set; } = "Bottom";

    /// <summary>Per-event beeps. Default off — speakers waking from sleep can clip the start beep.</summary>
    public bool BeepOnStart  { get; set; } = false;
    public bool BeepOnStop   { get; set; } = false;
    public bool BeepOnCancel { get; set; } = false;
    public double BeepVolume { get; set; } = 0.5;

    /// <summary>Trim leading/trailing silence with Silero VAD before transcription.</summary>
    public bool VadEnabled { get; set; } = true;
    public double VadThreshold { get; set; } = 0.3;
    public int VadPaddingMs { get; set; } = 500;

    /// <summary>Maximum mid-recording silence gap (ms) before VAD closes the speech segment.
    /// Set high enough to cover the longest thinking pause you'd ever make in one utterance.
    /// Default 30 000 ms (30 s) — effectively unlimited for normal dictation use.</summary>
    public int VadMaxSilenceMs { get; set; } = 30_000;

    /// <summary>Extra audio included from BEFORE the hotkey press. Catches the first syllable.</summary>
    public int PreRollMs { get; set; } = 250;

    /// <summary>Extra audio captured AFTER the hotkey release, in case the speaker trails off.</summary>
    public int PostRollMs { get; set; } = 200;

    public int HistoryLimit { get; set; } = 50;

    /// <summary>Parakeet | Whisper. Parakeet remains the default for existing installs.</summary>
    public string TranscriptionBackend { get; set; } = "Parakeet";

    /// <summary>tiny | base | small. Used only when TranscriptionBackend = Whisper.</summary>
    public string WhisperModel { get; set; } = "base";

    /// <summary>App language code for post-transcription filler-word removal ("en", "pt-BR", …).
    /// Matches upstream Handy's app language — selects which filler words get stripped.</summary>
    public string AppLanguage { get; set; } = "en";

    /// <summary>Custom filler-word list. null = use language defaults (upstream behaviour);
    /// empty list = disable filler-word removal entirely; non-empty list = use exactly these words.</summary>
    public List<string>? CustomFillerWords { get; set; } = null;

    [JsonIgnore]
    public string FilePath { get; private set; } = string.Empty;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
    };

    public static AppSettings Load(string dataDir)
    {
        var path = Path.Combine(dataDir, "settings.json");
        AppSettings s;
        bool hasSettingsVersion = false;
        if (File.Exists(path))
        {
            try
            {
                var json = File.ReadAllText(path);
                hasSettingsVersion = HasJsonProperty(json, "settingsVersion");
                s = JsonSerializer.Deserialize<AppSettings>(json, JsonOpts) ?? new AppSettings();
            }
            catch (Exception ex)
            {
                Log.Warn($"settings.json unreadable ({ex.Message}); using defaults");
                s = new AppSettings();
            }
        }
        else
        {
            s = new AppSettings();
        }
        s.FilePath = path;
        if (ApplyMigrations(s, dataDir, hasSettingsVersion))
            s.Save();
        return s;
    }

    public void Save()
    {
        if (string.IsNullOrEmpty(FilePath)) return;
        try
        {
            var json = JsonSerializer.Serialize(this, JsonOpts);
            File.WriteAllText(FilePath, json);
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to save settings: {ex.Message}");
        }
    }

    private static bool ApplyMigrations(AppSettings s, string dataDir, bool hasSettingsVersion)
    {
        var migrated = false;

        if (!hasSettingsVersion)
        {
            // Older saved settings kept the original VAD defaults, which were
            // too aggressive for quiet starts/ends of dictation.
            if (Math.Abs(s.VadThreshold - 0.5) < 0.0001)
            {
                s.VadThreshold = 0.3;
                migrated = true;
                Log.Info("Settings migration: VAD threshold 0.50 -> 0.30.");
            }
            if (s.VadPaddingMs == 200)
            {
                s.VadPaddingMs = 500;
                migrated = true;
                Log.Info("Settings migration: VAD padding 200ms -> 500ms.");
            }
        }

        if (!hasSettingsVersion || s.SettingsVersion != CurrentSettingsVersion)
        {
            s.SettingsVersion = CurrentSettingsVersion;
            migrated = true;
        }

        return migrated;
    }

    private static bool HasJsonProperty(string json, string propertyName)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object &&
                   doc.RootElement.TryGetProperty(propertyName, out _);
        }
        catch
        {
            return false;
        }
    }

}
