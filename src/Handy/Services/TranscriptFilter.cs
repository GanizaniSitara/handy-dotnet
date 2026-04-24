using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Handy.Services;

/// <summary>
/// Post-processes raw transcription output to remove filler words ("uh", "um", "hmm", …)
/// and collapse stutter artifacts before paste. Ports upstream Handy's
/// src-tauri/src/audio_toolkit/text.rs:filter_transcription_output verbatim —
/// upstream runs it on both Whisper and Parakeet output, not just Whisper.
/// </summary>
public static class TranscriptFilter
{
    private static readonly Regex MultiSpace = new(@"\s{2,}", RegexOptions.Compiled);

    // Language-specific filler-word lists, mirrored from upstream
    // audio_toolkit/text.rs:get_filler_words_for_language. Portuguese "um" means
    // "a/an", Spanish "ha" is the verb "has" — each locale opts in independently
    // so real words don't get stripped.
    private static readonly Dictionary<string, string[]> ByLang = new(StringComparer.OrdinalIgnoreCase)
    {
        ["en"] = new[] { "uh", "um", "uhm", "umm", "uhh", "uhhh", "ah", "hmm", "hm", "mmm", "mm", "mh", "eh", "ehh", "ha" },
        ["es"] = new[] { "ehm", "mmm", "hmm", "hm" },
        ["pt"] = new[] { "ahm", "hmm", "mmm", "hm" },
        ["fr"] = new[] { "euh", "hmm", "hm", "mmm" },
        ["de"] = new[] { "äh", "ähm", "hmm", "hm", "mmm" },
        ["it"] = new[] { "ehm", "hmm", "mmm", "hm" },
        ["cs"] = new[] { "ehm", "hmm", "mmm", "hm" },
        ["pl"] = new[] { "hmm", "mmm", "hm" },
        ["tr"] = new[] { "hmm", "mmm", "hm" },
        ["ru"] = new[] { "хм", "ммм", "hmm", "mmm" },
        ["uk"] = new[] { "хм", "ммм", "hmm", "mmm" },
        ["ar"] = new[] { "hmm", "mmm" },
        ["ja"] = new[] { "hmm", "mmm" },
        ["ko"] = new[] { "hmm", "mmm" },
        ["vi"] = new[] { "hmm", "mmm", "hm" },
        ["zh"] = new[] { "hmm", "mmm" },
    };

    // Conservative universal fallback — no "um", "eh", "ha" (they are real words in several languages).
    private static readonly string[] Fallback =
        { "uh", "uhm", "umm", "uhh", "uhhh", "ah", "hmm", "hm", "mmm", "mm", "mh", "ehh" };

    public static IReadOnlyList<string> FillersFor(string lang)
    {
        if (string.IsNullOrEmpty(lang)) return Fallback;
        var baseLang = lang.Split('-', '_')[0];
        return ByLang.TryGetValue(baseLang, out var list) ? list : Fallback;
    }

    /// <summary>
    /// Filters raw transcription text.
    /// </summary>
    /// <param name="text">Raw transcript.</param>
    /// <param name="lang">App language code (e.g. "en", "pt-BR"). Used when <paramref name="customFillerWords"/> is null.</param>
    /// <param name="customFillerWords">
    /// null → use language defaults. Non-null → use exactly this list (empty disables filler-word removal).
    /// Matches upstream's <c>Option&lt;Vec&lt;String&gt;&gt;</c> semantic.
    /// </param>
    public static string Filter(string text, string lang, IReadOnlyList<string>? customFillerWords)
    {
        if (string.IsNullOrEmpty(text)) return text ?? string.Empty;

        IReadOnlyList<string> words = customFillerWords is null
            ? FillersFor(lang)
            : customFillerWords;

        var filtered = text;
        foreach (var w in words)
        {
            if (string.IsNullOrEmpty(w)) continue;
            var pat = new Regex($@"(?i)\b{Regex.Escape(w)}\b[,.]?");
            filtered = pat.Replace(filtered, string.Empty);
        }

        filtered = CollapseStutters(filtered);
        filtered = MultiSpace.Replace(filtered, " ");
        return filtered.Trim();
    }

    // Collapse 3+ consecutive repetitions of the same alphabetic word to one
    // ("wh wh wh wh" → "wh"). Non-alphabetic tokens (numbers, punctuation) pass through.
    private static string CollapseStutters(string text)
    {
        var words = text.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return text;

        var result = new List<string>(words.Length);
        int i = 0;
        while (i < words.Length)
        {
            var w = words[i];
            if (w.All(char.IsLetter))
            {
                var lower = w.ToLowerInvariant();
                int count = 1;
                while (i + count < words.Length && words[i + count].ToLowerInvariant() == lower)
                    count++;
                result.Add(w);
                i += count >= 3 ? count : 1;
            }
            else
            {
                result.Add(w);
                i++;
            }
        }
        return string.Join(' ', result);
    }
}
