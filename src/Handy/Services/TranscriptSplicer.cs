namespace Handy.Services;

/// <summary>
/// Joins transcript fragments produced by additive background recognition.
/// </summary>
public static class TranscriptSplicer
{
    public static string Combine(string? prefix, string? tail)
    {
        if (string.IsNullOrWhiteSpace(prefix))
            return (tail ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(tail))
            return (prefix ?? string.Empty).Trim();

        var p = prefix.TrimEnd();
        var t = tail.TrimStart();
        if (p.Length == 0) return t;
        if (t.Length == 0) return p;
        return p + ' ' + t;
    }
}
