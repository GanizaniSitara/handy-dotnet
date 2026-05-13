using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Handy.Services;

public static class WhisperVocabularyPromptBuilder
{
    private const int DefaultMaxChars = 768;
    private const string Prefix = "Recognize these domain terms exactly when spoken: ";
    private const string Suffix = ".";

    public sealed record Result(string Prompt, int TermCount)
    {
        public bool HasPrompt => TermCount > 0 && !string.IsNullOrWhiteSpace(Prompt);
    }

    public static Result Build(IEnumerable<DomainCorrection>? corrections, int maxChars = DefaultMaxChars)
    {
        if (maxChars <= Prefix.Length + Suffix.Length)
            return new Result(string.Empty, 0);

        var terms = (corrections ?? Enumerable.Empty<DomainCorrection>())
            .Where(c => c is not null && c.Enabled)
            .Select(c => c.To?.Trim() ?? string.Empty)
            .Where(t => t.Length > 0)
            .Distinct(System.StringComparer.OrdinalIgnoreCase)
            .OrderBy(t => t, System.StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (terms.Count == 0)
            return new Result(string.Empty, 0);

        var prompt = new StringBuilder(Prefix);
        var count = 0;
        foreach (var term in terms)
        {
            var separator = count == 0 ? string.Empty : "; ";
            if (prompt.Length + separator.Length + term.Length + Suffix.Length > maxChars)
                break;

            prompt.Append(separator);
            prompt.Append(term);
            count++;
        }

        if (count == 0)
            return new Result(string.Empty, 0);

        prompt.Append(Suffix);
        return new Result(prompt.ToString(), count);
    }
}
