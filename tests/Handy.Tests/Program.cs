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

Console.WriteLine($"Domain correction fixture passed ({tests.Length} correction cases plus settings and prompt-builder checks).");

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

internal sealed record TestCase(
    string Name,
    string Input,
    IReadOnlyList<DomainCorrection> Rules,
    string Expected,
    int ExpectedCorrectionCount);
