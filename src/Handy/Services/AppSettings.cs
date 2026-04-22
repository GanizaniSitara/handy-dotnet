using System;
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
    public string Hotkey { get; set; } = "Ctrl+Alt+Space";
    public string CancelHotkey { get; set; } = "Escape";

    /// <summary>Push-to-talk (hold) vs. toggle.</summary>
    public bool PushToTalk { get; set; } = false;

    /// <summary>Ctrl+V | Direct | None | ShiftInsert | CtrlShiftV. Default matches
    /// upstream Handy: Direct (Unicode keystroke injection) works in both GUI apps
    /// and shells/TUIs (which often eat Ctrl+V as quoted-insert).</summary>
    public string PasteMethod { get; set; } = "Direct";

    public int PasteDelayMs { get; set; } = 50;

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

    public bool AudioFeedback { get; set; } = true;
    public double AudioFeedbackVolume { get; set; } = 0.5;

    /// <summary>Trim leading/trailing silence with Silero VAD before transcription.</summary>
    public bool VadEnabled { get; set; } = true;
    public double VadThreshold { get; set; } = 0.5;
    public int VadPaddingMs { get; set; } = 200;

    /// <summary>Extra audio included from BEFORE the hotkey press. Catches the first syllable.</summary>
    public int PreRollMs { get; set; } = 250;

    /// <summary>Extra audio captured AFTER the hotkey release, in case the speaker trails off.</summary>
    public int PostRollMs { get; set; } = 200;

    public int HistoryLimit { get; set; } = 50;

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
        if (File.Exists(path))
        {
            try
            {
                var json = File.ReadAllText(path);
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
}
