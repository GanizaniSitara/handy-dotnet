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

    public sealed record AppliedCorrection(string From, string To, int Count);
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

            var from = (correction.From ?? string.Empty).Trim();
            var to = (correction.To ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to))
                continue;

            var regex = BuildPhraseRegex(from);
            var count = 0;
            output = regex.Replace(output, _ =>
            {
                count++;
                return to;
            });

            if (count > 0)
                applied.Add(new AppliedCorrection(from, to, count));
        }

        return new Result(output, applied);
    }

    private static Regex BuildPhraseRegex(string phrase)
    {
        var tokens = Regex.Split(phrase.Trim(), @"\s+")
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(Regex.Escape);

        var body = string.Join(@"\s+", tokens);

        return new Regex(
            $@"(?<![{BoundaryChars}]){body}(?![{BoundaryChars}])",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    }
}
