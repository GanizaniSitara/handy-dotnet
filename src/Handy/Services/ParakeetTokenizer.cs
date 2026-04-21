using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Handy.Services;

/// <summary>
/// Loads NeMo Parakeet vocab.txt ("<token> <id>" per line) and maps ids → text.
/// Mirrors transcribe-rs/src/decode/tokens.rs:
///   - "▁" (U+2581) is replaced with a literal space at load time.
///   - "&lt;blk&gt;" is the blank token and its id is exposed via BlankId.
/// </summary>
internal sealed class ParakeetTokenizer
{
    private readonly string[] _idToToken;
    public int BlankId { get; }
    public int VocabSize => _idToToken.Length;

    // transcribe-rs post-process regex: "\A\s|\s\B|(\s)\b"
    //   - \A\s          → leading whitespace (drop)
    //   - \s\B          → whitespace not followed by a word boundary (drop)
    //   - (\s)\b        → whitespace at a word boundary (keep as single space)
    private static readonly Regex PostProcess = new(
        @"\A\s|\s\B|(\s)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public ParakeetTokenizer(string vocabPath)
    {
        var list = new List<string>();
        int blank = -1;

        foreach (var raw in File.ReadAllLines(vocabPath))
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;

            // Format: "<token> <id>". Split on the LAST space — the token
            // itself can contain leading characters but never a trailing space.
            var lastSpace = raw.LastIndexOf(' ');
            if (lastSpace < 0) continue;

            var token = raw.Substring(0, lastSpace);
            var idStr = raw.Substring(lastSpace + 1).Trim();
            if (!int.TryParse(idStr, out var id)) continue;

            while (list.Count <= id) list.Add(string.Empty);

            // ▁ (U+2581) is NeMo's space marker.
            list[id] = token.Replace("▁", " ");

            if (token == "<blk>") blank = id;
        }

        _idToToken = list.ToArray();
        BlankId = blank >= 0 ? blank : _idToToken.Length - 1;
    }

    public string Decode(IList<int> tokenIds)
    {
        var sb = new StringBuilder();
        foreach (var id in tokenIds)
        {
            if (id >= 0 && id < _idToToken.Length) sb.Append(_idToToken[id]);
        }

        var joined = sb.ToString();
        return PostProcess.Replace(joined, m =>
            m.Groups[1].Success ? " " : string.Empty);
    }
}
