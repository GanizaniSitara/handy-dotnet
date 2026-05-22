using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Handy.Services;

/// <summary>
/// Joins transcript fragments produced by additive background recognition.
///
/// The tail is decoded with a short audio lookback into the prefix region, so a
/// word straddling the splice point is fully re-decoded rather than clipped by
/// the VAD trims on either side. That makes the start of the tail transcript
/// overlap the end of the prefix transcript; <see cref="Combine"/> finds the
/// longest matching word run at the boundary and drops the duplicate, so the
/// boundary words survive without being doubled.
/// </summary>
public static class TranscriptSplicer
{
    // Cap on how far back from the boundary we look for a duplicated run. Bounds
    // the cost and avoids matching coincidental repeats far from the join.
    private const int MaxOverlapWords = 16;

    public static string Combine(string? prefix, string? tail)
    {
        var p = (prefix ?? string.Empty).Trim();
        var t = (tail ?? string.Empty).Trim();
        if (p.Length == 0) return t;
        if (t.Length == 0) return p;

        var prefixTokens = Tokenize(p);
        var tailTokens = Tokenize(t);

        var overlap = FindOverlap(prefixTokens, tailTokens);
        if (overlap == 0)
            return p + ' ' + t;

        var remainder = string.Join(' ', tailTokens.Skip(overlap).Select(tok => tok.Original)).Trim();
        return remainder.Length == 0 ? p : p + ' ' + remainder;
    }

    // Largest k (1..MaxOverlapWords) such that the last k normalized words of the
    // prefix equal the first k normalized words of the tail. Returns 0 if none.
    private static int FindOverlap(IReadOnlyList<Token> prefix, IReadOnlyList<Token> tail)
    {
        var max = Math.Min(MaxOverlapWords, Math.Min(prefix.Count, tail.Count));
        for (var k = max; k >= 1; k--)
        {
            var match = true;
            for (var i = 0; i < k; i++)
            {
                var a = prefix[prefix.Count - k + i].Norm;
                var b = tail[i].Norm;
                if (a.Length == 0 || a != b) { match = false; break; }
            }
            if (match) return k;
        }
        return 0;
    }

    private readonly record struct Token(string Original, string Norm);

    private static List<Token> Tokenize(string text)
    {
        var tokens = new List<Token>();
        foreach (var word in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            tokens.Add(new Token(word, Normalize(word)));
        return tokens;
    }

    // Lowercase, letters/digits only — so casing and punctuation differences
    // between the two decodes don't defeat the overlap match.
    private static string Normalize(string word)
    {
        var sb = new StringBuilder(word.Length);
        foreach (var c in word)
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }
}
