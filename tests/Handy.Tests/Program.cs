using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Handy.Services;

var tests = new[]
{
    new TestCase(
        "multi-word business products",
        "Please open azure dev ops and service now.",
        new[]
        {
            Rule("azure dev ops", "Azure DevOps"),
            Rule("service now", "ServiceNow"),
        },
        "Please open Azure DevOps and ServiceNow.",
        2),

    new TestCase(
        "phrase boundaries",
        "The microservice now runs service now checks.",
        new[] { Rule("service now", "ServiceNow") },
        "The microservice now runs ServiceNow checks.",
        1),

    new TestCase(
        "case-insensitive canonical casing",
        "Schedule the contoso atlas review.",
        new[] { Rule("contoso atlas", "Contoso Atlas") },
        "Schedule the Contoso Atlas review.",
        1),

    new TestCase(
        "punctuation stays outside match",
        "Route this through service now, then update azure dev ops.",
        new[]
        {
            Rule("service now", "ServiceNow"),
            Rule("azure dev ops", "Azure DevOps"),
        },
        "Route this through ServiceNow, then update Azure DevOps.",
        2),

    new TestCase(
        "flexible whitespace",
        "The quarterly business     review is ready.",
        new[] { Rule("quarterly business review", "QBR") },
        "The QBR is ready.",
        1),

    new TestCase(
        "disabled rule",
        "Leave service now alone.",
        new[] { Rule("service now", "ServiceNow", enabled: false) },
        "Leave service now alone.",
        0),

    new TestCase(
        "hyphenated compounds are bounded",
        "The service-now migration mentions service now.",
        new[] { Rule("service now", "ServiceNow") },
        "The service-now migration mentions ServiceNow.",
        1),

    new TestCase(
        "possessives are bounded",
        "The contoso's plan references contoso.",
        new[] { Rule("contoso", "Contoso") },
        "The contoso's plan references Contoso.",
        1),

    new TestCase(
        "required context gates ambiguous term",
        "Please create a service now ticket.",
        new[] { Rule("service now", "ServiceNow", requiredContext: new[] { "ticket" }) },
        "Please create a ServiceNow ticket.",
        1),

    new TestCase(
        "missing required context leaves normal words",
        "We can service now and review later.",
        new[] { Rule("service now", "ServiceNow", requiredContext: new[] { "ticket" }) },
        "We can service now and review later.",
        0),

    new TestCase(
        "blocked context suppresses ambiguous term",
        "We can service now please.",
        new[] { Rule("service now", "ServiceNow", blockedContext: new[] { "please" }) },
        "We can service now please.",
        0),

    new TestCase(
        "variants share canonical term",
        "Open a snow incident ticket.",
        new[]
        {
            Rule(
                "service now",
                "ServiceNow",
                variants: new[] { "service now", "snow" },
                requiredContext: new[] { "ticket" }),
        },
        "Open a ServiceNow incident ticket.",
        1),

    new TestCase(
        "case sensitive matching",
        "abc ABC abc.",
        new[] { Rule("abc", "ABC", caseSensitive: true) },
        "ABC ABC ABC.",
        2),
};

foreach (var test in tests)
{
    var result = DomainCorrectionService.Apply(test.Input, test.Rules);
    AssertEqual(test.Expected, result.Text, $"{test.Name}: text");
    AssertEqual(test.ExpectedCorrectionCount, result.Corrections.Sum(c => c.Count), $"{test.Name}: correction count");
}

AssertDisabledRulePersists();
AssertWhisperVocabularyPromptBuilder();
AssertSpeculativeCachePolicy();
AssertGpuModelDetection();
AssertTranscriptSplicer();
AssertTaskCaptureSettingsMigration();
AssertCancelChordSettingsMigration();
AssertTaskCaptureWriter();
AssertPasteTargetPolicy();
AssertTextInjectionService();

Console.WriteLine($"Handy fixture passed ({tests.Length} correction cases plus settings, prompt-builder, speculative, splicer, task-capture, paste-target, and text-injection checks).");

static DomainCorrection Rule(
    string from,
    string to,
    bool enabled = true,
    string[]? variants = null,
    string[]? requiredContext = null,
    string[]? blockedContext = null,
    bool caseSensitive = false,
    string notes = "") =>
    new()
    {
        Enabled = enabled,
        From = from,
        To = to,
        Variants = variants?.ToList() ?? new List<string>(),
        RequiredContext = requiredContext?.ToList() ?? new List<string>(),
        BlockedContext = blockedContext?.ToList() ?? new List<string>(),
        CaseSensitive = caseSensitive,
        Notes = notes,
    };

static void AssertEqual<T>(T expected, T actual, string label)
{
    if (EqualityComparer<T>.Default.Equals(expected, actual))
        return;

    throw new InvalidOperationException($"{label}: expected <{expected}> but got <{actual}>");
}

static void AssertDisabledRulePersists()
{
    var dir = Path.Combine(Path.GetTempPath(), "Handy.Tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try
    {
        var settings = AppSettings.Load(dir);
        settings.TaskCaptureHotkey = "Ctrl+Alt+T";
        settings.TaskCaptureInbox = Path.Combine(dir, "task-inbox");
        settings.AlwaysCopyTranscriptToClipboard = true;
        settings.PasteFocusPolicy = "RestoreAndPaste";
        settings.WhisperVocabularyPromptEnabled = true;
        settings.WhisperCarryInitialPrompt = false;
        settings.DomainCorrections = new List<DomainCorrection>
        {
            Rule(
                "service now",
                "ServiceNow",
                enabled: false,
                variants: new[] { "service now", "snow" },
                requiredContext: new[] { "ticket", "incident" },
                blockedContext: new[] { "weather" },
                caseSensitive: true,
                notes: "ITSM product"),
        };
        settings.Save();

        var reloaded = AppSettings.Load(dir);
        AssertEqual("Ctrl+Alt+T", reloaded.TaskCaptureHotkey, "settings round-trip: task capture hotkey");
        AssertEqual(Path.Combine(dir, "task-inbox"), reloaded.TaskCaptureInbox, "settings round-trip: task capture inbox");
        AssertEqual(true, reloaded.AlwaysCopyTranscriptToClipboard, "settings round-trip: always-copy clipboard");
        AssertEqual("RestoreAndPaste", reloaded.PasteFocusPolicy, "settings round-trip: paste focus policy");
        AssertEqual(1, reloaded.DomainCorrections.Count, "settings round-trip: rule count");
        var rule = reloaded.DomainCorrections[0];
        AssertEqual(true, reloaded.WhisperVocabularyPromptEnabled, "settings round-trip: whisper vocabulary prompt");
        AssertEqual(false, reloaded.WhisperCarryInitialPrompt, "settings round-trip: whisper carry prompt");
        AssertEqual(false, rule.Enabled, "settings round-trip: disabled state");
        AssertEqual("service now", rule.From, "settings round-trip: from");
        AssertEqual("ServiceNow", rule.To, "settings round-trip: to");
        AssertEqual("service now; snow", string.Join("; ", rule.Variants), "settings round-trip: variants");
        AssertEqual("ticket; incident", string.Join("; ", rule.RequiredContext), "settings round-trip: required context");
        AssertEqual("weather", string.Join("; ", rule.BlockedContext), "settings round-trip: blocked context");
        AssertEqual(true, rule.CaseSensitive, "settings round-trip: case sensitivity");
        AssertEqual("ITSM product", rule.Notes, "settings round-trip: notes");
    }
    finally
    {
        try { Directory.Delete(dir, recursive: true); } catch { }
    }
}

static void AssertTaskCaptureSettingsMigration()
{
    var dir = Path.Combine(Path.GetTempPath(), "Handy.Tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try
    {
        File.WriteAllText(
            Path.Combine(dir, "settings.json"),
            """
            {
              "settingsVersion": 2,
              "hotkey": "Ctrl+Alt+Space",
              "cancelHotkey": "Escape"
            }
            """);

        var migrated = AppSettings.Load(dir);

        AssertEqual(4, migrated.SettingsVersion, "task capture migration: settings version");
        AssertEqual("Ctrl+Space", migrated.Hotkey, "task capture migration: ordinary hotkey");
        AssertEqual("Ctrl+Shift+Space", migrated.TaskCaptureHotkey, "task capture migration: intake hotkey");
    }
    finally
    {
        try { Directory.Delete(dir, recursive: true); } catch { }
    }
}

static void AssertCancelChordSettingsMigration()
{
    var dir = Path.Combine(Path.GetTempPath(), "Handy.Tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try
    {
        var defaults = AppSettings.Load(dir);
        AssertEqual("Alt+Shift+X", defaults.CancelChordHotkey, "cancel chord: fresh default");
        var cancel = Hotkey.Parse(defaults.CancelChordHotkey);
        AssertEqual(Hotkey.Mods.Alt | Hotkey.Mods.Shift, cancel.Required, "cancel chord: modifiers");
        AssertEqual((uint)'X', cancel.Vk, "cancel chord: key");
        foreach (var chord in new[] { defaults.Hotkey, defaults.TaskCaptureHotkey, defaults.CancelHotkey,
                                     defaults.CopyLastHotkey, defaults.RetypeLastHotkey })
            AssertEqual(false, Hotkey.Parse(chord).Vk == cancel.Vk, "cancel chord: no default key collision");

        foreach (var chord in new[] { "Ctrl+Shift+X", "ctrl+shift+x", "Ctrl+Alt+Q", "Alt+Shift+X" })
        {
            File.WriteAllText(Path.Combine(dir, "settings.json"), JsonSerializer.Serialize(new
            {
                settingsVersion = 3,
                cancelChordHotkey = chord,
                cancelChordEnabled = false,
            }));
            var loaded = AppSettings.Load(dir);
            var expected = chord.Equals("Ctrl+Shift+X", StringComparison.OrdinalIgnoreCase) ? "Alt+Shift+X" : chord;
            AssertEqual(expected, loaded.CancelChordHotkey, "cancel chord: migration or custom preservation");
            AssertEqual(false, loaded.CancelChordEnabled, "cancel chord: disabled state preserved");
            using var saved = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "settings.json")));
            AssertEqual(expected, saved.RootElement.GetProperty("cancelChordHotkey").GetString(), "cancel chord: persisted");
            AssertEqual(expected, AppSettings.Load(dir).CancelChordHotkey, "cancel chord: reload stable");
        }
    }
    finally
    {
        try { Directory.Delete(dir, recursive: true); } catch { }
    }
}

static void AssertTaskCaptureWriter()
{
    var dir = Path.Combine(Path.GetTempPath(), "Handy.Tests", Guid.NewGuid().ToString("N"));
    var inbox = Path.Combine(dir, "inbox");
    try
    {
        var foreground = new ForegroundWindowSnapshot(
            123,
            "codex",
            456,
            "ConsoleWindowClass",
            "Command Prompt");
        var started = new DateTimeOffset(2026, 7, 30, 10, 0, 0, TimeSpan.Zero);

        var result = TaskCaptureWriter.Write(
            inbox,
            "raw dictated task",
            "Dictated task",
            started,
            foreground);

        AssertEqual(true, File.Exists(result.Path), "task capture writer: final file exists");
        AssertEqual(0, Directory.GetFiles(inbox, "*.tmp").Length, "task capture writer: no temporary files");
        using var doc = JsonDocument.Parse(File.ReadAllText(result.Path));
        var root = doc.RootElement;
        AssertEqual(1, root.GetProperty("schema_version").GetInt32(), "task capture writer: schema");
        AssertEqual(result.CaptureId, root.GetProperty("capture_id").GetString(), "task capture writer: capture id");
        AssertEqual("raw dictated task", root.GetProperty("raw_transcript").GetString(), "task capture writer: raw");
        AssertEqual("Dictated task", root.GetProperty("transcript").GetString(), "task capture writer: transcript");
        AssertEqual(123, root.GetProperty("foreground").GetProperty("pid").GetInt32(), "task capture writer: foreground pid");
        AssertEqual("task-inbox", root.GetProperty("destination").GetString(), "task capture writer: destination");
    }
    finally
    {
        try { Directory.Delete(dir, recursive: true); } catch { }
    }
}

static void AssertWhisperVocabularyPromptBuilder()
{
    var result = WhisperVocabularyPromptBuilder.Build(new[]
    {
        Rule("service now", "ServiceNow", variants: new[] { "service now", "snow" }),
        Rule("azure dev ops", "Azure DevOps"),
        Rule("disabled", "DisabledTerm", enabled: false),
        Rule("duplicate", "servicenow"),
    });

    AssertEqual(2, result.TermCount, "whisper prompt: term count");
    AssertEqual(
        "Recognize these domain terms exactly when spoken: Azure DevOps; ServiceNow.",
        result.Prompt,
        "whisper prompt: canonical terms only");

    var empty = WhisperVocabularyPromptBuilder.Build(new[] { Rule("disabled", "DisabledTerm", enabled: false) });
    AssertEqual(false, empty.HasPrompt, "whisper prompt: disabled rules omitted");
}

static void AssertGpuModelDetection()
{
    var root = Path.Combine(Path.GetTempPath(), "handy-gpu-detect-" + Guid.NewGuid().ToString("N"));
    try
    {
        var gpu = Directory.CreateDirectory(Path.Combine(root, "gpu")).FullName;
        var cpu = Directory.CreateDirectory(Path.Combine(root, "cpu")).FullName;
        foreach (var f in new[] { "encoder-model.onnx", "decoder_joint-model.int8.onnx" })
            File.WriteAllText(Path.Combine(gpu, f), "");
        foreach (var f in new[] { "encoder-model.int8.onnx", "decoder_joint-model.int8.onnx" })
            File.WriteAllText(Path.Combine(cpu, f), "");
        AssertEqual(true, ParakeetTranscriptionService.HasFp32Models(gpu), "gpu model: fp32 encoder + int8 decoder is complete");
        AssertEqual(false, ParakeetTranscriptionService.HasInt8Models(gpu), "gpu model: is not mistaken for the CPU build");
        AssertEqual(false, ParakeetTranscriptionService.HasFp32Models(cpu), "cpu model: is not mistaken for the GPU build");
        AssertEqual(true, ParakeetTranscriptionService.HasInt8Models(cpu), "cpu model: int8 pair is complete");
    }
    finally { try { Directory.Delete(root, true); } catch { } }
}

static void AssertSpeculativeCachePolicy()
{
    var s = new AppSettings { BackgroundRecognitionEnabled = true };
    SileroVadService.Analysis Tail(int count, SileroVadService.SpeechStatus status)
        => new(new float[count], status, 0);
    AssertEqual(SpeculativeCachePolicy.Decision.UsePrefixOnly,
        SpeculativeCachePolicy.Decide(96000, 80000, Tail(16000, SileroVadService.SpeechStatus.Silence), s),
        "spec policy: explicit silence uses prefix despite preserved audio");
    AssertEqual(SpeculativeCachePolicy.Decision.UsePrefixOnly,
        SpeculativeCachePolicy.Decide(96000, 96000, null, s), "spec policy: exact snapshot");
    AssertEqual(SpeculativeCachePolicy.Decision.UsePrefixOnly,
        SpeculativeCachePolicy.Decide(97000, 96000, null, s), "spec policy: sub-frame tail is post-roll jitter");
    AssertEqual(SpeculativeCachePolicy.Decision.UsePrefixOnly,
        SpeculativeCachePolicy.Decide(97000, 96000, Tail(1000, SileroVadService.SpeechStatus.Uncertain), s),
        "spec policy: sub-frame tail even when VAD could not frame it");
    AssertEqual(SpeculativeCachePolicy.Decision.UsePrefixPlusTail,
        SpeculativeCachePolicy.Decide(128000, 96000, Tail(32000, SileroVadService.SpeechStatus.Speech), s),
        "spec policy: substantial speech tail");
    foreach (int count in new[] { 1000, 4000, 7900 })
        AssertEqual(SpeculativeCachePolicy.Decision.ColdPass,
            SpeculativeCachePolicy.Decide(128000, 96000, Tail(count, SileroVadService.SpeechStatus.Speech), s),
            "spec policy: short speech is never discarded " + count);
    AssertEqual(SpeculativeCachePolicy.Decision.ColdPass,
        SpeculativeCachePolicy.Decide(96000, 80000, null, s), "spec policy: unanalyzed tail");
    AssertEqual(SpeculativeCachePolicy.Decision.ColdPass,
        SpeculativeCachePolicy.Decide(96000, 80000, Tail(16000, SileroVadService.SpeechStatus.Uncertain), s),
        "spec policy: uncertain quiet tail");
    AssertEqual(SpeculativeCachePolicy.Decision.ColdPass,
        SpeculativeCachePolicy.Decide(96000, 0, null, s), "spec policy: no snapshot");
    AssertEqual(SpeculativeCachePolicy.Decision.ColdPass,
        SpeculativeCachePolicy.Decide(80000, 96000, null, s), "spec policy: shorter final buffer");
    AssertEqual(SpeculativeCachePolicy.Decision.ColdPass,
        SpeculativeCachePolicy.Decide(96000, 80000, Tail(16000, SileroVadService.SpeechStatus.Silence),
            new AppSettings { BackgroundRecognitionEnabled = false }), "spec policy: disabled");
}

static void AssertPasteTargetPolicy()
{
    // Wrong-window detection. Unknown handles must not block a paste — we only
    // refuse when we can actually prove the target moved.
    AssertEqual(false, PasteTargetPolicy.IsWrongWindow(0x1234, 0x1234), "paste target: same window proceeds");
    AssertEqual(true,  PasteTargetPolicy.IsWrongWindow(0x1234, 0x5678), "paste target: changed window refused");
    AssertEqual(false, PasteTargetPolicy.IsWrongWindow(0, 0x5678), "paste target: unknown intended proceeds");
    AssertEqual(false, PasteTargetPolicy.IsWrongWindow(0x1234, 0), "paste target: unknown actual proceeds");

    // Policy parsing and fallback
    AssertEqual(PasteFocusPolicy.RestoreAndPaste, PasteTargetPolicy.ParseFocusPolicy(null), "policy parse null");
    AssertEqual(PasteFocusPolicy.RestoreAndPaste, PasteTargetPolicy.ParseFocusPolicy(""), "policy parse empty");
    AssertEqual(PasteFocusPolicy.RefuseAndCopy, PasteTargetPolicy.ParseFocusPolicy("RefuseAndCopy"), "policy parse refuse");
    AssertEqual(PasteFocusPolicy.RefuseAndCopy, PasteTargetPolicy.ParseFocusPolicy("refuse"), "policy parse refuse lower");
    AssertEqual(PasteFocusPolicy.RestoreAndPaste, PasteTargetPolicy.ParseFocusPolicy("RestoreAndPaste"), "policy parse restore");
    AssertEqual(PasteFocusPolicy.RestoreAndPaste, PasteTargetPolicy.ParseFocusPolicy("restore"), "policy parse restore lower");
    AssertEqual(PasteFocusPolicy.PasteAnyway, PasteTargetPolicy.ParseFocusPolicy("PasteAnyway"), "policy parse anyway");
    AssertEqual(PasteFocusPolicy.RestoreAndPaste, PasteTargetPolicy.ParseFocusPolicy("unknown"), "policy parse unknown fallback");
    AssertEqual("RestoreAndPaste", new AppSettings().PasteFocusPolicy, "policy default is RestoreAndPaste");

    // ShouldBlockOnFocusMismatch
    AssertEqual(false, PasteTargetPolicy.ShouldBlockOnFocusMismatch(PasteFocusPolicy.RefuseAndCopy, 0x1234, 0x1234), "should block: same window false");
    AssertEqual(true,  PasteTargetPolicy.ShouldBlockOnFocusMismatch(PasteFocusPolicy.RefuseAndCopy, 0x1234, 0x5678), "should block: changed window refuse");
    AssertEqual(false, PasteTargetPolicy.ShouldBlockOnFocusMismatch(PasteFocusPolicy.PasteAnyway, 0x1234, 0x5678), "should block: changed window anyway");
    AssertEqual(false, PasteTargetPolicy.ShouldBlockOnFocusMismatch(PasteFocusPolicy.RefuseAndCopy, 0, 0x5678), "should block: unknown intended false");

    // Delivery classification.
    AssertEqual(PasteOutcome.Delivered, PasteTargetPolicy.Classify(sent: 800, expected: 800, lastErr: 0),
        "paste classify: full injection delivered");
    AssertEqual(PasteOutcome.Delivered, PasteTargetPolicy.Classify(sent: 0, expected: 0, lastErr: 0),
        "paste classify: nothing to send is delivered");

    // UIPI: an elevated target accepts nothing and reports access denied.
    AssertEqual(PasteOutcome.Refused, PasteTargetPolicy.Classify(sent: 0, expected: 800, lastErr: 5),
        "paste classify: elevated target refuses everything");
    AssertEqual(PasteOutcome.Refused, PasteTargetPolicy.Classify(sent: 0, expected: 800, lastErr: 0),
        "paste classify: zero accepted is a refusal even without an error code");
    AssertEqual(PasteOutcome.Refused, PasteTargetPolicy.Classify(sent: 400, expected: 800, lastErr: 5),
        "paste classify: partial with access-denied is a refusal");

    // A short count with no access-denied is a partial write, not a refusal.
    AssertEqual(PasteOutcome.Partial, PasteTargetPolicy.Classify(sent: 400, expected: 800, lastErr: 0),
        "paste classify: short injection is partial");

    // focusHeld:false wins over everything — it means we stopped deliberately.
    AssertEqual(PasteOutcome.Interrupted,
        PasteTargetPolicy.Classify(sent: 320, expected: 320, lastErr: 0, focusHeld: false),
        "paste classify: focus loss mid-injection is an interruption");

    AssertEqual(true,  PasteOutcome.Delivered.IsDelivered(), "paste outcome: delivered");
    AssertEqual(false, PasteOutcome.WrongWindow.IsDelivered(), "paste outcome: wrong window not delivered");
    AssertEqual("wrongWindow", PasteOutcome.WrongWindow.ToDiagToken(), "paste outcome: diag token");
    AssertEqual("ok", PasteOutcome.Delivered.ToDiagToken(), "paste outcome: delivered diag token");
}

static void AssertTranscriptSplicer()
{
    AssertEqual("hello world",
        TranscriptSplicer.Combine("hello ", " world"),
        "splicer: normalizes boundary spacing");

    AssertEqual("tail only",
        TranscriptSplicer.Combine(" ", " tail only "),
        "splicer: blank prefix");

    var combined = TranscriptSplicer.Combine("Please open service", "now ticket.");
    AssertEqual("Please open service now ticket.", combined, "splicer: boundary phrase preserved");

    var corrected = DomainCorrectionService.Apply(
        combined,
        new[] { Rule("service now", "ServiceNow") });
    AssertEqual("Please open ServiceNow ticket.", corrected.Text, "splicer: corrections see boundary phrase");

    // Lookback overlap: the tail re-decodes a few words from the end of the
    // prefix; the duplicated run is dropped, not doubled.
    AssertEqual("I was going to the shop now",
        TranscriptSplicer.Combine("I was going to the", "going to the shop now"),
        "splicer: dedups overlapping word run");

    // Casing and punctuation differences between the two decodes don't defeat it.
    AssertEqual("open Service Now please",
        TranscriptSplicer.Combine("open Service Now", "service now, please"),
        "splicer: overlap match ignores case and punctuation");

    // A genuine repeated word is preserved (only the matched overlap is removed).
    AssertEqual("I think I think so",
        TranscriptSplicer.Combine("I think I", "I think so"),
        "splicer: legitimate repeat preserved");

    // No coincidental merge when there is no real overlap.
    AssertEqual("the cat sat on the mat",
        TranscriptSplicer.Combine("the cat sat", "on the mat"),
        "splicer: no false merge without overlap");
}

static void AssertTextInjectionService()
{
    var settings = new AppSettings { PasteMethod = "Direct", DirectCharDelayMs = 0 };

    // 1. Same window proceeds and delivers all characters
    {
        var mock = new MockWindowBridge { ForegroundWindow = new IntPtr(0x1000) };
        var injector = new TextInjectionService(mock);
        var res = injector.Paste("Hello World", settings, new IntPtr(0x1000));
        AssertEqual(PasteOutcome.Delivered, res.Outcome, "injection: same window delivered");
        AssertEqual(11, res.DeliveredChars, "injection: chars delivered count");
        AssertEqual("Hello World", new string(mock.SentChars.ToArray()), "injection: chars match");
    }

    // 2. Changed window under RefuseAndCopy
    {
        var mock = new MockWindowBridge { ForegroundWindow = new IntPtr(0x2000) };
        var injector = new TextInjectionService(mock);
        settings.PasteFocusPolicy = "RefuseAndCopy";
        var res = injector.Paste("Hello World", settings, new IntPtr(0x1000));
        AssertEqual(PasteOutcome.WrongWindow, res.Outcome, "injection: refuse on changed window");
        AssertEqual(0, res.DeliveredChars, "injection: 0 chars delivered");
        AssertEqual(0, mock.SentChars.Count, "injection: no chars sent");
        AssertEqual(true, res.Detail?.Contains("0x2000"), "injection: detail names thief window");
    }

    // 3. Changed window under PasteAnyway
    {
        var mock = new MockWindowBridge { ForegroundWindow = new IntPtr(0x2000) };
        var injector = new TextInjectionService(mock);
        settings.PasteFocusPolicy = "PasteAnyway";
        var res = injector.Paste("Hello World", settings, new IntPtr(0x1000));
        AssertEqual(PasteOutcome.Delivered, res.Outcome, "injection: paste anyway delivers");
        AssertEqual(11, res.DeliveredChars, "injection: paste anyway chars count");
        AssertEqual("Hello World", new string(mock.SentChars.ToArray()), "injection: paste anyway chars match");
    }

    // 4. Changed window under RestoreAndPaste - restore succeeds
    {
        var mock = new MockWindowBridge
        {
            ForegroundWindow = new IntPtr(0x2000),
            AllowRestore = true,
        };
        var injector = new TextInjectionService(mock);
        settings.PasteFocusPolicy = "RestoreAndPaste";
        var res = injector.Paste("Hello World", settings, new IntPtr(0x1000));
        AssertEqual(PasteOutcome.Delivered, res.Outcome, "injection: restore succeeds delivers");
        AssertEqual(new IntPtr(0x1000), mock.RestoredWindow, "injection: restore targeted intended window");
        AssertEqual(11, res.DeliveredChars, "injection: restore chars count");
    }

    // 5. Changed window under RestoreAndPaste - restore fails
    {
        var mock = new MockWindowBridge
        {
            ForegroundWindow = new IntPtr(0x2000),
            AllowRestore = false,
        };
        var injector = new TextInjectionService(mock);
        settings.PasteFocusPolicy = "RestoreAndPaste";
        var res = injector.Paste("Hello World", settings, new IntPtr(0x1000));
        AssertEqual(PasteOutcome.WrongWindow, res.Outcome, "injection: restore fails returns WrongWindow");
        AssertEqual(0, res.DeliveredChars, "injection: 0 chars delivered");
        AssertEqual(0, mock.SentChars.Count, "injection: 0 chars sent");
    }

    // 6. Mid-injection focus steal under RefuseAndCopy
    {
        var mock = new MockWindowBridge { ForegroundWindow = new IntPtr(0x1000) };
        mock.OnUnicodeChar = (count) =>
        {
            if (count == 8) mock.ForegroundWindow = new IntPtr(0x9999);
        };
        var injector = new TextInjectionService(mock);
        settings.PasteFocusPolicy = "RefuseAndCopy";
        var text = "12345678901234567890123456789012"; // 32 chars
        var res = injector.Paste(text, settings, new IntPtr(0x1000));
        AssertEqual(PasteOutcome.Interrupted, res.Outcome, "injection: mid-steal interrupted");
        AssertEqual(16, res.DeliveredChars, "injection: mid-steal chars delivered");
        AssertEqual(16, mock.SentChars.Count, "injection: mid-steal chars count");
    }

    // 7. Mid-injection focus steal under PasteAnyway
    {
        var mock = new MockWindowBridge { ForegroundWindow = new IntPtr(0x1000) };
        mock.OnUnicodeChar = (count) =>
        {
            if (count == 8) mock.ForegroundWindow = new IntPtr(0x9999);
        };
        var injector = new TextInjectionService(mock);
        settings.PasteFocusPolicy = "PasteAnyway";
        var text = "12345678901234567890123456789012";
        var res = injector.Paste(text, settings, new IntPtr(0x1000));
        AssertEqual(PasteOutcome.Delivered, res.Outcome, "injection: mid-steal paste anyway delivers");
        AssertEqual(32, res.DeliveredChars, "injection: mid-steal all chars delivered");
    }

    // 8. Mid-injection focus steal under RestoreAndPaste (restore succeeds)
    {
        var mock = new MockWindowBridge { ForegroundWindow = new IntPtr(0x1000), AllowRestore = true };
        mock.OnUnicodeChar = (count) =>
        {
            if (count == 8) mock.ForegroundWindow = new IntPtr(0x9999);
        };
        var injector = new TextInjectionService(mock);
        settings.PasteFocusPolicy = "RestoreAndPaste";
        var text = "12345678901234567890123456789012";
        var res = injector.Paste(text, settings, new IntPtr(0x1000));
        AssertEqual(PasteOutcome.Delivered, res.Outcome, "injection: mid-steal restored delivers");
        AssertEqual(32, res.DeliveredChars, "injection: mid-steal restored all chars");
    }

    // 9. Mid-injection focus steal under RestoreAndPaste (restore fails)
    {
        var mock = new MockWindowBridge { ForegroundWindow = new IntPtr(0x1000), AllowRestore = false };
        mock.OnUnicodeChar = (count) =>
        {
            if (count == 8) mock.ForegroundWindow = new IntPtr(0x9999);
        };
        var injector = new TextInjectionService(mock);
        settings.PasteFocusPolicy = "RestoreAndPaste";
        var text = "12345678901234567890123456789012";
        var res = injector.Paste(text, settings, new IntPtr(0x1000));
        AssertEqual(PasteOutcome.Interrupted, res.Outcome, "injection: mid-steal restore failed interrupted");
        AssertEqual(16, res.DeliveredChars, "injection: mid-steal restore failed 16 chars");
    }

    // 10. Chord paste (CtrlV) wrong window refused
    {
        var mock = new MockWindowBridge { ForegroundWindow = new IntPtr(0x2000) };
        var injector = new TextInjectionService(mock);
        var chordSettings = new AppSettings { PasteMethod = "CtrlV", PasteFocusPolicy = "RefuseAndCopy" };
        var res = injector.Paste("Hello Chord", chordSettings, new IntPtr(0x1000));
        AssertEqual(PasteOutcome.WrongWindow, res.Outcome, "chord: wrong window refused");
        AssertEqual(0u, mock.SentKeyCount, "chord: no keys sent");
    }

    // 11. Chord paste (CtrlV) delivers when window matches
    {
        var mock = new MockWindowBridge { ForegroundWindow = new IntPtr(0x1000) };
        var injector = new TextInjectionService(mock);
        var chordSettings = new AppSettings { PasteMethod = "CtrlV", PasteFocusPolicy = "RefuseAndCopy" };
        var res = injector.Paste("Hello Chord", chordSettings, new IntPtr(0x1000));
        AssertEqual(PasteOutcome.Delivered, res.Outcome, "chord: delivered");
        AssertEqual(4u, mock.SentKeyCount, "chord: 4 keys sent (Ctrl down, V down, V up, Ctrl up)");
    }

    // 12. Elevation mismatch UIPI error description
    {
        var mock = new MockWindowBridge
        {
            ForegroundWindow = new IntPtr(0x1000),
            Elevated = true,
            ElevatedProcessName = "TaskMgr",
            LastWin32Error = 5,
            BlockSendInput = true,
        };
        var injector = new TextInjectionService(mock);
        var chordSettings = new AppSettings { PasteMethod = "CtrlV", PasteFocusPolicy = "RefuseAndCopy" };
        var res = injector.Paste("Hello Admin", chordSettings, new IntPtr(0x1000));
        AssertEqual(PasteOutcome.Refused, res.Outcome, "chord: UIPI refused");
        AssertEqual(true, res.Detail?.Contains("TaskMgr is running as administrator"), "chord: UIPI detail names admin process");
    }

    // 13. Empty text handling
    {
        var mock = new MockWindowBridge { ForegroundWindow = new IntPtr(0x1000) };
        var injector = new TextInjectionService(mock);
        var res = injector.Paste("", settings, new IntPtr(0x1000));
        AssertEqual(PasteOutcome.Delivered, res.Outcome, "injection: empty string delivered");
    }

    // 14. None paste method executes auto-submit only
    {
        var mock = new MockWindowBridge { ForegroundWindow = new IntPtr(0x1000) };
        var injector = new TextInjectionService(mock);
        var noneSettings = new AppSettings { PasteMethod = "None", AutoSubmitKey = "Enter" };
        var res = injector.Paste("No Paste", noneSettings, new IntPtr(0x1000));
        AssertEqual(PasteOutcome.Delivered, res.Outcome, "none method: delivered");
        AssertEqual(2u, mock.SentKeyCount, "none method: auto-submit sent 2 keys");
        AssertEqual(0, mock.SentChars.Count, "none method: no chars typed");
    }

    // 15. Direct into Citrix reports it, so the caller can leave a recovery copy
    {
        var mock = new MockWindowBridge { ForegroundWindow = new IntPtr(0x1000), Citrix = true };
        var injector = new TextInjectionService(mock);
        var res = injector.Paste("Remote", new AppSettings { PasteMethod = "Direct" }, new IntPtr(0x1000));
        AssertEqual(PasteOutcome.Delivered, res.Outcome, "citrix: delivered");
        AssertEqual(true, res.Citrix, "citrix: flagged on result");
    }

    // Identified Gemini consoles receive a single framed batch for every paste
    // mode, including recovery. No protocol markers enter the clipboard.
    foreach (var method in new[] { "Direct", "CtrlV", "ShiftInsert" })
    foreach (var intended in new[] { new IntPtr(0x1000), IntPtr.Zero })
    {
        var mock = new MockWindowBridge { BracketedPaste = true };
        var res = new TextInjectionService(mock).Paste("one\r\n£漢😀\rtwo", new AppSettings
            { PasteMethod = method, AutoSubmitKey = "None" }, intended);
        AssertEqual(PasteOutcome.Delivered, res.Outcome, "gemini: batch delivered");
        AssertEqual("\x1b[200~one\n£漢😀\ntwo\x1b[201~", mock.BatchText, "gemini: exact frame");
        AssertEqual(1, mock.BatchCalls, "gemini: one batch");
        AssertEqual(0, mock.SentChars.Count, "gemini: no ordinary character loop");
        AssertEqual(0u, mock.SentKeyCount, "gemini: no paste chord or submit");
        AssertEqual<string?>(null, mock.ClipboardContent, "gemini: clipboard untouched");
    }
    foreach (var accepted in new[] { 0u, 2u })
    {
        var mock = new MockWindowBridge { BracketedPaste = true, BatchAccepted = accepted };
        var res = new TextInjectionService(mock).Paste("test", new AppSettings
            { PasteMethod = "Direct", AutoSubmitKey = "Enter" }, mock.ForegroundWindow);
        AssertEqual(accepted == 0 ? PasteOutcome.Refused : PasteOutcome.Partial, res.Outcome, "gemini: batch failure detected");
        AssertEqual(0u, mock.SentKeyCount, "gemini: failed paste never submits");
        AssertEqual(0, mock.SentChars.Count, "gemini: failed batch never retries as Direct");
    }
    {
        var mock = new MockWindowBridge { BracketedPaste = true };
        var res = new TextInjectionService(mock).Paste(new string('x', 10000), new AppSettings
            { PasteMethod = "Direct", AutoSubmitKey = "Enter" }, mock.ForegroundWindow);
        AssertEqual(PasteOutcome.Delivered, res.Outcome, "gemini: optional submit delivered");
        AssertEqual(4000, mock.SleptMs, "gemini: long paste settling bounded");
        AssertEqual(2u, mock.SentKeyCount, "gemini: one separate Enter pair");
    }
    {
        var mock = new MockWindowBridge { BracketedPaste = true };
        mock.OnProbe = () => mock.ForegroundWindow = new IntPtr(0x9999);
        var res = new TextInjectionService(mock).Paste("test", new AppSettings { PasteMethod = "Direct" }, new IntPtr(0x1000));
        AssertEqual(PasteOutcome.WrongWindow, res.Outcome, "gemini: focus changed during process probe");
        AssertEqual(0, mock.BatchCalls, "gemini: no batch into changed target");
    }
    {
        var mock = new MockWindowBridge { BracketedPaste = true };
        mock.OnSleep = () => mock.ForegroundWindow = new IntPtr(0x9999);
        var res = new TextInjectionService(mock).Paste("test", new AppSettings
            { PasteMethod = "Direct", AutoSubmitKey = "Enter" }, mock.ForegroundWindow);
        AssertEqual(PasteOutcome.Interrupted, res.Outcome, "gemini: focus changed before submit");
        AssertEqual(0u, mock.SentKeyCount, "gemini: no Enter into changed target");
    }
    {
        var mock = new MockWindowBridge { BracketedPaste = true };
        new TextInjectionService(mock).Paste("disabled", new AppSettings { PasteMethod = "None" }, mock.ForegroundWindow);
        AssertEqual(0, mock.BatchCalls, "gemini: None remains disabled");
    }
    foreach (var row in new[]
    {
        ("agy.exe", "", true),
        ("GEMINI.EXE", "", true),
        ("node.exe", "node.exe C:\\npm\\node_modules\\@google\\gemini-cli\\bundle\\gemini.js", true),
        ("node.exe", "\"C:\\Program Files\\node.exe\" \"C:\\npm dir\\@google\\gemini-cli\\dist\\index.js\"", true),
        ("node.exe", "node.exe C:/npm/@google/gemini-cli/bundle/gemini.js", true),
        ("node.exe", "node.exe C:/npm/@google/gemini-cli-other/index.js", false),
        ("node.exe", "node.exe app.js --prompt C:/npm/@google/gemini-cli/bundle/gemini.js", false),
        ("cmd.exe", "cmd.exe echo C:/npm/@google/gemini-cli/index.js", false),
        ("claude.exe", "Gemini", false),
    }) AssertEqual(row.Item3, ConsolePasteTarget.IsGeminiClient(row.Item1, row.Item2), "gemini process classifier: " + row.Item1);

    // 16. Direct into Windows Terminal pastes in one go instead of typing per key
    {
        var mock = new MockWindowBridge { ForegroundWindow = new IntPtr(0x1000), WindowsTerminal = true };
        var injector = new TextInjectionService(mock);
        var res = injector.Paste("Terminal text", new AppSettings { PasteMethod = "Direct" }, new IntPtr(0x1000));
        AssertEqual(PasteOutcome.Delivered, res.Outcome, "wt: delivered");
        AssertEqual(0, mock.SentChars.Count, "wt: no per-char typing");
        AssertEqual("Terminal text", mock.ClipboardContent, "wt: transcript went via clipboard");
    }
    // A prepared AGY profile selects one clipboard operation without changing
    // the configured method for an unrelated console.
    {
        var mock = new MockWindowBridge { ClipboardMethod = "CtrlV" };
        var res = new TextInjectionService(mock).Paste("AGY text", new AppSettings { PasteMethod = "Direct" }, mock.ForegroundWindow);
        AssertEqual(PasteOutcome.Delivered, res.Outcome, "agy: chord accepted");
        AssertEqual(0, mock.SentChars.Count, "agy: no character events");
        AssertEqual(4u, mock.SentKeyCount, "agy: one clipboard chord");
        AssertEqual("AGY text", mock.ClipboardContent, "agy: text available to app");
    }
    {
        var mock = new MockWindowBridge();
        var res = new TextInjectionService(mock).Paste("Other console", new AppSettings { PasteMethod = "Direct" }, mock.ForegroundWindow);
        AssertEqual(13, mock.SentChars.Count, "unknown console: configured Direct retained");
    }
    {
        var mock = new MockWindowBridge { ClipboardMethod = "CtrlV" };
        new TextInjectionService(mock).Paste("Retype", new AppSettings { PasteMethod = "Direct" }, IntPtr.Zero);
        AssertEqual(6, mock.SentChars.Count, "agy: recovery retains Direct");
        AssertEqual(0u, mock.SentKeyCount, "agy: prepared route does not override recovery");
    }
    {
        var mock = new MockWindowBridge { ClipboardMethod = "CtrlV" };
        var res = new TextInjectionService(mock).Paste("Disabled", new AppSettings { PasteMethod = "None" }, mock.ForegroundWindow);
        AssertEqual(0u, mock.SentKeyCount, "agy: explicit None retained");
        AssertEqual(0, mock.SentChars.Count, "agy: None emits no text");
    }
    {
        var mock = new MockWindowBridge();
        mock.OnSleep = () => mock.ForegroundWindow = new IntPtr(0x9999);
        var res = new TextInjectionService(mock).Paste("Keep me", new AppSettings
            { PasteMethod = "CtrlV", PasteDelayMs = 50, PasteFocusPolicy = "RefuseAndCopy" }, mock.ForegroundWindow);
        AssertEqual(PasteOutcome.WrongWindow, res.Outcome, "clipboard: focus steal during delay refused");
        AssertEqual(0u, mock.SentKeyCount, "clipboard: no chord into changed window");
        AssertEqual("Keep me", mock.ClipboardContent, "clipboard: recovery text preserved");
    }
}

sealed class MockWindowBridge : IWindowBridge
{
    public IntPtr ForegroundWindow { get; set; } = new(0x1000);
    public IntPtr RestoredWindow { get; set; } = IntPtr.Zero;
    public bool AllowRestore { get; set; } = false;
    public bool Citrix { get; set; } = false;
    public bool Elevated { get; set; } = false;
    public string ElevatedProcessName { get; set; } = "AdminApp";
    public int LastWin32Error { get; set; } = 0;
    public uint SentUnicodeCount { get; set; } = 0;
    public uint SentKeyCount { get; set; } = 0;
    public bool BlockSendInput { get; set; } = false;
    public List<char> SentChars { get; } = new();
    public List<(ushort vk, bool down)> SentKeys { get; } = new();
    public string? ClipboardContent { get; set; }
    public bool FailSetClipboard { get; set; } = false;

    public Action<int>? OnUnicodeChar;

    public IntPtr GetForegroundWindow() => ForegroundWindow;

    public bool SetForegroundWindow(IntPtr hWnd)
    {
        RestoredWindow = hWnd;
        if (AllowRestore)
        {
            ForegroundWindow = hWnd;
            return true;
        }
        return false;
    }

    public string DescribeWindow(IntPtr hwnd) => $"[MockApp|Window|0x{hwnd.ToInt64():X}]";

    public string DescribeForegroundWindow() => DescribeWindow(ForegroundWindow);

    public bool IsCitrixForeground() => Citrix;

    public bool WindowsTerminal { get; set; } = false;
    public bool IsWindowsTerminalForeground() => WindowsTerminal;
    public string? ClipboardMethod { get; set; }
    public bool BracketedPaste { get; set; }
    public string? BatchText { get; set; }
    public int BatchCalls { get; set; }
    public uint? BatchAccepted { get; set; }
    public Action? OnProbe { get; set; }
    public bool UseBracketedPaste(IntPtr hwnd)
    {
        OnProbe?.Invoke();
        return BracketedPaste;
    }
    public uint SendUnicodeText(string text)
    {
        BatchText = text;
        BatchCalls++;
        return BatchAccepted ?? (uint)text.Length * 2;
    }
    public string? PreferredClipboardMethod(IntPtr hwnd) => ClipboardMethod;
    public Action? OnSleep { get; set; }

    public bool IsElevationMismatch(IntPtr hwnd, out string targetName, out int lastErr)
    {
        lastErr = LastWin32Error;
        targetName = ElevatedProcessName;
        return Elevated;
    }

    public uint SendUnicode(char ch)
    {
        if (BlockSendInput) return 0;
        OnUnicodeChar?.Invoke(SentChars.Count);
        SentChars.Add(ch);
        SentUnicodeCount += 2;
        return 2;
    }

    public uint SendKey(ushort vk, bool down)
    {
        if (BlockSendInput) return 0;
        SentKeys.Add((vk, down));
        SentKeyCount += 1;
        return 1;
    }

    public bool TrySetClipboard(string text)
    {
        if (FailSetClipboard) return false;
        ClipboardContent = text;
        return true;
    }

    public string? TryReadClipboard() => ClipboardContent;

    public int SleptMs { get; set; }
    public void Sleep(int ms)
    {
        SleptMs += ms;
        OnSleep?.Invoke();
    }

    public int GetLastWin32Error() => LastWin32Error;
}

internal sealed record TestCase(
    string Name,
    string Input,
    IReadOnlyList<DomainCorrection> Rules,
    string Expected,
    int ExpectedCorrectionCount);
