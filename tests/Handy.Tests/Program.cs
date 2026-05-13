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
};

foreach (var test in tests)
{
    var result = DomainCorrectionService.Apply(test.Input, test.Rules);
    AssertEqual(test.Expected, result.Text, $"{test.Name}: text");
    AssertEqual(test.ExpectedCorrectionCount, result.Corrections.Sum(c => c.Count), $"{test.Name}: correction count");
}

AssertDisabledRulePersists();

Console.WriteLine($"Domain correction fixture passed ({tests.Length} correction cases plus settings round-trip).");

static DomainCorrection Rule(string from, string to, bool enabled = true) =>
    new() { Enabled = enabled, From = from, To = to };

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
        settings.DomainCorrections = new List<DomainCorrection>
        {
            Rule("service now", "ServiceNow", enabled: false),
        };
        settings.Save();

        var reloaded = AppSettings.Load(dir);
        AssertEqual(true, reloaded.AlwaysCopyTranscriptToClipboard, "settings round-trip: always-copy clipboard");
        AssertEqual(1, reloaded.DomainCorrections.Count, "settings round-trip: rule count");
        AssertEqual(false, reloaded.DomainCorrections[0].Enabled, "settings round-trip: disabled state");
        AssertEqual("service now", reloaded.DomainCorrections[0].From, "settings round-trip: from");
        AssertEqual("ServiceNow", reloaded.DomainCorrections[0].To, "settings round-trip: to");
    }
    finally
    {
        try { Directory.Delete(dir, recursive: true); } catch { }
    }
}

internal sealed record TestCase(
    string Name,
    string Input,
    IReadOnlyList<DomainCorrection> Rules,
    string Expected,
    int ExpectedCorrectionCount);
