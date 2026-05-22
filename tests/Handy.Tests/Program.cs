using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
AssertTranscriptSplicer();

Console.WriteLine($"Domain correction fixture passed ({tests.Length} correction cases plus settings, prompt-builder, speculative, and splicer checks).");

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
        settings.AlwaysCopyTranscriptToClipboard = true;
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
        AssertEqual(true, reloaded.AlwaysCopyTranscriptToClipboard, "settings round-trip: always-copy clipboard");
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

static void AssertSpeculativeCachePolicy()
{
    var s = new AppSettings { BackgroundRecognitionEnabled = true };

    // Tail is silence (VAD trimmed everything after the snapshot) -> use prefix only.
    AssertEqual(SpeculativeCachePolicy.Decision.UsePrefixOnly,
        SpeculativeCachePolicy.Decide(finalRawSampleCount: 96_000, snapshotRawSampleCount: 80_000, tailVadSampleCount: 0, s),
        "spec policy: tail-silent -> prefix only");

    // Final equals snapshot (rare: snapshot caught the whole thing) -> prefix only.
    AssertEqual(SpeculativeCachePolicy.Decision.UsePrefixOnly,
        SpeculativeCachePolicy.Decide(finalRawSampleCount: 96_000, snapshotRawSampleCount: 96_000, tailVadSampleCount: 0, s),
        "spec policy: final == snapshot -> prefix only");

    // Final has 2 s of new speech beyond the snapshot, with voiced VAD output -> decode tail.
    AssertEqual(SpeculativeCachePolicy.Decision.UsePrefixPlusTail,
        SpeculativeCachePolicy.Decide(finalRawSampleCount: 128_000, snapshotRawSampleCount: 96_000, tailVadSampleCount: 32_000, s),
        "spec policy: voiced tail -> prefix + tail");

    // Tail VAD output is below the silence floor (<=100 ms) -> treat as silence, prefix only.
    AssertEqual(SpeculativeCachePolicy.Decision.UsePrefixOnly,
        SpeculativeCachePolicy.Decide(finalRawSampleCount: 128_000, snapshotRawSampleCount: 96_000, tailVadSampleCount: 1_000, s),
        "spec policy: sub-floor tail -> prefix only");

    // Tail has real speech but is short (250 ms of voiced VAD) -> cold pass, never dropped.
    // This is the bug fix: previously this final phrase was discarded as "too short".
    AssertEqual(SpeculativeCachePolicy.Decision.ColdPass,
        SpeculativeCachePolicy.Decide(finalRawSampleCount: 128_000, snapshotRawSampleCount: 96_000, tailVadSampleCount: 4_000, s),
        "spec policy: short real tail -> cold pass (not dropped)");

    // Just below the reliable-tail threshold (~480 ms voiced) -> still cold pass, not dropped.
    AssertEqual(SpeculativeCachePolicy.Decision.ColdPass,
        SpeculativeCachePolicy.Decide(finalRawSampleCount: 160_000, snapshotRawSampleCount: 96_000, tailVadSampleCount: 7_900, s),
        "spec policy: sub-reliable real tail -> cold pass");

    // No snapshot at all -> cold pass.
    AssertEqual(SpeculativeCachePolicy.Decision.ColdPass,
        SpeculativeCachePolicy.Decide(finalRawSampleCount: 96_000, snapshotRawSampleCount: 0, tailVadSampleCount: 0, s),
        "spec policy: no snapshot -> cold pass");

    // Feature disabled -> cold pass regardless.
    var off = new AppSettings { BackgroundRecognitionEnabled = false };
    AssertEqual(SpeculativeCachePolicy.Decision.ColdPass,
        SpeculativeCachePolicy.Decide(finalRawSampleCount: 96_000, snapshotRawSampleCount: 80_000, tailVadSampleCount: 0, off),
        "spec policy: feature flag off -> cold pass");
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

internal sealed record TestCase(
    string Name,
    string Input,
    IReadOnlyList<DomainCorrection> Rules,
    string Expected,
    int ExpectedCorrectionCount);
