using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

    /// <summary>Per-character sleep (ms) for Direct paste mode into LOCAL apps.
    /// 0 = max speed (native desktops). The Citrix-detected path uses
    /// <see cref="DirectCharDelayMsCitrix"/> instead.</summary>
    public int DirectCharDelayMs { get; set; } = 0;

    /// <summary>Per-character sleep (ms) for Direct paste mode when the foreground
    /// window belongs to a Citrix client (CDViewer / wfica32 / Receiver / Workspace
    /// app). Citrix's keystroke forwarding can drop or reorder Unicode events at
    /// full speed, so we throttle just this path. Local pastes stay fast.</summary>
    public int DirectCharDelayMsCitrix { get; set; } = 1;

    public bool AppendTrailingSpace { get; set; } = false;

    /// <summary>DontModify | CopyToClipboard — upstream ClipboardHandling.</summary>
    public string ClipboardHandling { get; set; } = "DontModify";

    /// <summary>When true, every successful final transcript is left on the clipboard
    /// after paste/auto-submit handling, overriding ClipboardHandling restore semantics.</summary>
    public bool AlwaysCopyTranscriptToClipboard { get; set; } = false;

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

    /// <summary>When true, writes the last raw capture and post-VAD ASR input to
    /// %APPDATA%\Handy\diagnostics for troubleshooting audio-path problems.</summary>
    public bool SaveLastAudioForDiagnostics { get; set; } = false;

    /// <summary>EXPERIMENTAL. While recording, detect natural pauses in speech and
    /// kick off a speculative ASR pass on the audio captured so far. On hotkey
    /// release, reuse that pass as the prefix and only decode the audio tail
    /// captured after the snapshot. If the tail is silence, skip final ASR.
    /// Off by default; opt in by setting "backgroundRecognitionEnabled": true.</summary>
    public bool BackgroundRecognitionEnabled { get; set; } = false;

    /// <summary>Minimum silence duration (ms) before a speculative ASR pass is
    /// triggered. Lower = more snapshots, more CPU; higher = miss short pauses.</summary>
    public int BackgroundPauseTriggerMs { get; set; } = 600;

    /// <summary>Minimum new audio captured (ms) since the last completed speculative
    /// snapshot before another snapshot is allowed. Prevents back-to-back ASR runs
    /// when the user pauses, resumes briefly, pauses again.</summary>
    public int BackgroundMinNewSpeechMs { get; set; } = 1500;

    /// <summary>OnLevels max-bar value below which a 50 ms block is treated as
    /// silence for pause detection. 0.15 ≈ −43 dBFS on the normalized bar curve.</summary>
    public double BackgroundSilenceBarThreshold { get; set; } = 0.15;

    public int HistoryLimit { get; set; } = 50;

    /// <summary>Parakeet | Whisper. Parakeet remains the default for existing installs.</summary>
    public string TranscriptionBackend { get; set; } = "Parakeet";

    /// <summary>Auto | V2 | V3. Used only when TranscriptionBackend = Parakeet.
    /// Auto: prefer v3 if installed, else v2 (legacy behaviour).
    /// V2 / V3: force that variant; if the requested files aren't on disk, fall back to whatever's installed.</summary>
    public string ParakeetVariant { get; set; } = "Auto";

    /// <summary>tiny.en | tiny | base | base.en | small | small.en. Used only when TranscriptionBackend = Whisper.</summary>
    public string WhisperModel { get; set; } = "base";

    /// <summary>When using Whisper, seed the recognizer with canonical glossary terms.</summary>
    public bool WhisperVocabularyPromptEnabled { get; set; } = false;

    /// <summary>Repeat the initial vocabulary prompt across Whisper decode windows.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public bool WhisperCarryInitialPrompt { get; set; } = true;

    /// <summary>App language code for post-transcription filler-word removal ("en", "pt-BR", …).
    /// Matches upstream Handy's app language — selects which filler words get stripped.</summary>
    public string AppLanguage { get; set; } = "en";

    /// <summary>Custom filler-word list. null = use language defaults (upstream behaviour);
    /// empty list = disable filler-word removal entirely; non-empty list = use exactly these words.</summary>
    public List<string>? CustomFillerWords { get; set; } = null;

    /// <summary>Explicit post-filter phrase corrections for domain vocabulary.
    /// Empty by default so ordinary dictation is never changed unless the user opts in.</summary>
    public List<DomainCorrection> DomainCorrections { get; set; } = new();

    /// <summary>Quiet | Normal | Verbose | Debug. Controls which INFO lines appear
    /// in the in-app Log tab. WARN and ERROR always pass through. Default Normal —
    /// shows Raw / Filter / Transcript / Paste / startup, hides VAD/ASR/HOOK noise.</summary>
    public string LogDisplayVerbosity { get; set; } = "Normal";

    /// <summary>Quiet | Normal | Verbose | Debug. Controls which INFO lines are
    /// written to handy.log on disk. Default Debug — keep the full forensic trail
    /// even if the in-app display is filtered down for readability.</summary>
    public string LogFileVerbosity { get; set; } = "Debug";

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

        s.DomainCorrections ??= new List<DomainCorrection>();

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

public sealed class DomainCorrection
{
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public bool Enabled { get; set; } = true;

    /// <summary>Legacy single-variant field. Kept for settings compatibility;
    /// new UI edits write the first variant here as well.</summary>
    public string From { get; set; } = string.Empty;

    /// <summary>Canonical replacement text.</summary>
    public string To { get; set; } = string.Empty;

    /// <summary>Heard forms that may be rewritten to the canonical term.</summary>
    public List<string> Variants { get; set; } = new();

    /// <summary>At least one of these phrases must be near the matched variant.
    /// Empty means the rule is ungated.</summary>
    public List<string> RequiredContext { get; set; } = new();

    /// <summary>If any of these phrases are near the matched variant, the rule is skipped.</summary>
    public List<string> BlockedContext { get; set; } = new();

    public bool CaseSensitive { get; set; } = false;

    public string Notes { get; set; } = string.Empty;

    [JsonIgnore]
    public string VariantsText
    {
        get => Join(EffectiveVariants());
        set
        {
            Variants = SplitList(value);
            From = Variants.Count > 0 ? Variants[0] : string.Empty;
        }
    }

    [JsonIgnore]
    public string RequiredContextText
    {
        get => Join(RequiredContext);
        set => RequiredContext = SplitList(value);
    }

    [JsonIgnore]
    public string BlockedContextText
    {
        get => Join(BlockedContext);
        set => BlockedContext = SplitList(value);
    }

    public List<string> EffectiveVariants()
    {
        var values = Clean(Variants);
        if (values.Count == 0 && !string.IsNullOrWhiteSpace(From))
            values.Add(From.Trim());
        return values;
    }

    public DomainCorrection Clone() => new()
    {
        Enabled = Enabled,
        From = From,
        To = To,
        Variants = Clean(Variants),
        RequiredContext = Clean(RequiredContext),
        BlockedContext = Clean(BlockedContext),
        CaseSensitive = CaseSensitive,
        Notes = Notes,
    };

    public void NormalizeForSave()
    {
        Variants = EffectiveVariants();
        RequiredContext = Clean(RequiredContext);
        BlockedContext = Clean(BlockedContext);
        From = Variants.Count > 0 ? Variants[0] : string.Empty;
        To = To?.Trim() ?? string.Empty;
        Notes = Notes?.Trim() ?? string.Empty;
    }

    private static string Join(IEnumerable<string>? values) =>
        string.Join("; ", Clean(values));

    private static List<string> SplitList(string? text) =>
        Clean((text ?? string.Empty)
            .Split(new[] { ';', ',' }, System.StringSplitOptions.RemoveEmptyEntries));

    private static List<string> Clean(IEnumerable<string>? values) =>
        (values ?? Enumerable.Empty<string>())
            .Select(v => v?.Trim() ?? string.Empty)
            .Where(v => v.Length > 0)
            .Distinct(System.StringComparer.OrdinalIgnoreCase)
            .ToList();
}
