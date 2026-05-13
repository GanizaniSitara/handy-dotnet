using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Handy.Services;

/// <summary>
/// Applies explicit user-configured phrase corrections after generic transcript
/// cleanup. Rules are opt-in and whole-phrase matched so ordinary words are not
/// rewritten unless a specific correction says to do so.
/// </summary>
public static class DomainCorrectionService
{
    private const string BoundaryChars = @"\p{L}\p{N}_'\-";
    private const int ContextWindowChars = 96;

    public sealed record AppliedCorrection(string From, string To, int Count, string Reason);
    public sealed record Result(string Text, IReadOnlyList<AppliedCorrection> Corrections);

    public static Result Apply(string? text, IReadOnlyList<DomainCorrection>? corrections)
    {
        if (string.IsNullOrEmpty(text) || corrections is null || corrections.Count == 0)
            return new Result(text ?? string.Empty, Array.Empty<AppliedCorrection>());

        var output = text;
        var applied = new List<AppliedCorrection>();

        foreach (var correction in corrections)
        {
            if (correction is null || !correction.Enabled)
                continue;

            var to = (correction.To ?? string.Empty).Trim();
            var variants = correction.EffectiveVariants();
            if (variants.Count == 0 || string.IsNullOrWhiteSpace(to))
                continue;

            foreach (var from in variants)
            {
                var regex = BuildPhraseRegex(from, correction.CaseSensitive);
                var count = 0;
                var reasons = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                output = regex.Replace(output, match =>
                {
                    var context = ContextAround(output, match);
                    if (!ContextAllows(context, correction, out var reason))
                        return match.Value;

                    count++;
                    reasons.Add(reason);
                    return to;
                });

                if (count > 0)
                    applied.Add(new AppliedCorrection(from, to, count, string.Join(", ", reasons)));
            }
        }

        return new Result(output, applied);
    }

    private static bool ContextAllows(string context, DomainCorrection correction, out string reason)
    {
        var blocked = FirstContextMatch(context, correction.BlockedContext, correction.CaseSensitive);
        if (blocked is not null)
        {
            reason = $"blocked={blocked}";
            return false;
        }

        var required = (correction.RequiredContext ?? new List<string>())
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .ToList();
        if (required.Count == 0)
        {
            reason = "ungated";
            return true;
        }

        var match = FirstContextMatch(context, required, correction.CaseSensitive);
        if (match is not null)
        {
            reason = $"required={match}";
            return true;
        }

        reason = "missing-required-context";
        return false;
    }

    private static string? FirstContextMatch(string context, IEnumerable<string>? phrases, bool caseSensitive)
    {
        foreach (var phrase in phrases ?? Enumerable.Empty<string>())
        {
            var trimmed = phrase.Trim();
            if (trimmed.Length == 0)
                continue;
            if (BuildPhraseRegex(trimmed, caseSensitive).IsMatch(context))
                return trimmed;
        }
        return null;
    }

    private static string ContextAround(string text, Match match)
    {
        var start = Math.Max(0, match.Index - ContextWindowChars);
        var end = Math.Min(text.Length, match.Index + match.Length + ContextWindowChars);
        return text[start..end];
    }

    private static Regex BuildPhraseRegex(string phrase, bool caseSensitive)
    {
        var tokens = Regex.Split(phrase.Trim(), @"\s+")
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(Regex.Escape);

        var body = string.Join(@"\s+", tokens);

        return new Regex(
            $@"(?<![{BoundaryChars}]){body}(?![{BoundaryChars}])",
            RegexOptions.Compiled |
            RegexOptions.CultureInvariant |
            (caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase));
    }
}
