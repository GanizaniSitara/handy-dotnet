namespace Handy.Services;

/// <summary>Why a paste was not delivered, or that it was.</summary>
public enum PasteOutcome
{
    /// <summary>Every event we asked for was accepted.</summary>
    Delivered,
    /// <summary>Focus moved between the hotkey and the paste; we never injected.</summary>
    WrongWindow,
    /// <summary>Focus moved part-way through injection; the transcript was cut.</summary>
    Interrupted,
    /// <summary>Windows refused the injection outright — typically UIPI (elevated target).</summary>
    Refused,
    /// <summary>Some events were accepted and some were not, with no focus change seen.</summary>
    Partial,
}

/// <summary>Action to take when the foreground window has changed since dictation started.</summary>
public enum PasteFocusPolicy
{
    /// <summary>Refuse to type into the changed window; copy transcript to clipboard and notify.</summary>
    RefuseAndCopy,
    /// <summary>Attempt to restore focus to the intended window; fall back to clipboard if unsuccessful.</summary>
    RestoreAndPaste,
    /// <summary>Type into whatever window currently has focus anyway.</summary>
    PasteAnyway,
}

/// <summary>
/// Pure decision logic for "should we type this, and did it land?". Kept free of
/// window handles and Win32 calls so it can be unit-tested directly, the same
/// way <see cref="SpeculativeCachePolicy"/> is.
/// </summary>
public static class PasteTargetPolicy
{
    /// <summary>ERROR_ACCESS_DENIED — what SendInput reports when UIPI blocks it.</summary>
    public const int ErrorAccessDenied = 5;

    /// <summary>
    /// Parses a string into a <see cref="PasteFocusPolicy"/>, defaulting to <see cref="PasteFocusPolicy.RestoreAndPaste"/>.
    /// </summary>
    public static PasteFocusPolicy ParseFocusPolicy(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "refuseandcopy" or "refuse"     => PasteFocusPolicy.RefuseAndCopy,
            "pasteanyway" or "anyway"       => PasteFocusPolicy.PasteAnyway,
            _                               => PasteFocusPolicy.RestoreAndPaste,
        };

    /// <summary>
    /// True when the window we are about to type into is not the one the user
    /// was looking at when they started dictating.
    ///
    /// A zero on either side means "unknown" — we can't prove a mismatch, so we
    /// proceed rather than block a legitimate paste on missing information.
    /// </summary>
    public static bool IsWrongWindow(long intendedHwnd, long actualHwnd)
    {
        if (intendedHwnd == 0 || actualHwnd == 0) return false;
        return intendedHwnd != actualHwnd;
    }

    /// <summary>
    /// Evaluates whether the injection should be blocked given the active policy
    /// and the intended vs actual window handles.
    /// </summary>
    public static bool ShouldBlockOnFocusMismatch(PasteFocusPolicy policy, long intendedHwnd, long actualHwnd)
    {
        if (policy == PasteFocusPolicy.PasteAnyway) return false;
        return IsWrongWindow(intendedHwnd, actualHwnd);
    }

    /// <summary>
    /// Classifies the result of an injection from the event counts and the last
    /// Win32 error.
    ///
    /// <paramref name="focusHeld"/> is false when the injection loop noticed the
    /// foreground window change mid-flight — that distinguishes "another app
    /// stole focus half way through" from "Windows refused us".
    /// </summary>
    public static PasteOutcome Classify(uint sent, uint expected, int lastErr, bool focusHeld = true)
    {
        if (!focusHeld) return PasteOutcome.Interrupted;
        if (expected == 0) return PasteOutcome.Delivered;
        if (sent >= expected) return PasteOutcome.Delivered;
        if (sent == 0) return PasteOutcome.Refused;
        return lastErr == ErrorAccessDenied ? PasteOutcome.Refused : PasteOutcome.Partial;
    }

    /// <summary>Did the transcript actually reach the target?</summary>
    public static bool IsDelivered(this PasteOutcome outcome) => outcome == PasteOutcome.Delivered;

    /// <summary>
    /// Short, greppable token for the per-dictation Diag line. Matches the
    /// existing lower-camel style of the other outcome values.
    /// </summary>
    public static string ToDiagToken(this PasteOutcome outcome) => outcome switch
    {
        PasteOutcome.Delivered   => "ok",
        PasteOutcome.WrongWindow => "wrongWindow",
        PasteOutcome.Interrupted => "pasteInterrupted",
        PasteOutcome.Refused     => "pasteRefused",
        PasteOutcome.Partial     => "pastePartial",
        _                        => "pasteFailed",
    };
}
