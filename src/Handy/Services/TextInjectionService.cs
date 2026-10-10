using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using Handy.PInvoke;

namespace Handy.Services;

/// <summary>
/// Writes a transcript into the foreground app using the method configured in
/// AppSettings. Mirrors upstream's PasteMethod + ClipboardHandling + AutoSubmitKey.
/// </summary>
/// <summary>
/// What happened to one paste attempt. <see cref="DeliveredChars"/> is only
/// meaningful for the Direct method, where injection is per-character and can
/// therefore be cut off part-way. <see cref="Citrix"/> is true when the text
/// was typed into a Citrix viewer, where a remote window switch is invisible to
/// us and "delivered" cannot be trusted to mean "landed where intended".
/// </summary>
public sealed record PasteResult(PasteOutcome Outcome, string? Detail = null, int DeliveredChars = 0, bool Citrix = false)
{
    public bool Delivered => Outcome.IsDelivered();
}

public sealed class TextInjectionService
{
    /// <summary>
    /// How often the Direct injection loop re-checks that focus hasn't moved.
    /// Small enough that a steal costs a few characters, large enough that the
    /// extra GetForegroundWindow calls don't show up in the latency budget.
    /// </summary>
    private const int FocusCheckEveryChars = 16;

    // Modifier chords must be sent in three stages: press the modifier(s),
    // press+release the action key, sleep, then release the modifier(s).
    // Sending a single batched 4-event SendInput collapses the Ctrl-up and
    // V-up into the same keyboard tick and Windows Terminal (plus several
    // other terminal-style apps) drops the chord. Upstream's enigo path
    // sleeps 100 ms between click and modifier release; we do the same.
    private const int ChordHoldMs = 100;

    private readonly IWindowBridge _bridge;

    public TextInjectionService() : this(new DefaultWindowBridge()) { }

    public TextInjectionService(IWindowBridge bridge)
    {
        _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
    }

    /// <param name="intendedHwnd">
    /// Window that had focus when the user started dictating, or 0 if unknown.
    /// Anything else on screen at paste time is not where the text belongs.
    /// </param>
    public PasteResult Paste(string text, AppSettings settings, IntPtr intendedHwnd)
    {
        if (string.IsNullOrEmpty(text)) return new PasteResult(PasteOutcome.Delivered);

        var method = (settings.PasteMethod ?? "CtrlV").Trim();
        Log.Info($"Paste: method={method}, len={text.Length}, fg={_bridge.DescribeForegroundWindow()}");

        var actualHwnd = _bridge.GetForegroundWindow();
        var policy = PasteTargetPolicy.ParseFocusPolicy(settings.PasteFocusPolicy);

        // Refuse to type into a window the user wasn't looking at, unless configured
        // otherwise (PasteAnyway) or successfully restored (RestoreAndPaste).
        if (PasteTargetPolicy.IsWrongWindow(intendedHwnd.ToInt64(), actualHwnd.ToInt64()))
        {
            if (policy == PasteFocusPolicy.PasteAnyway)
            {
                Log.Info($"Paste: target window changed (intended=0x{intendedHwnd.ToInt64():X}, actual={_bridge.DescribeWindow(actualHwnd)}), but PasteAnyway policy is active; proceeding.");
            }
            else if (policy == PasteFocusPolicy.RestoreAndPaste && TryRestoreFocus(intendedHwnd))
            {
                actualHwnd = _bridge.GetForegroundWindow();
                Log.Info($"Paste: target window changed; restored focus to intended window 0x{intendedHwnd.ToInt64():X}.");
            }
            else
            {
                var thief = _bridge.DescribeWindow(actualHwnd);
                Log.Warn($"Paste: target window changed since dictation started — not typing. " +
                         $"intended=0x{intendedHwnd.ToInt64():X} actual={thief}");
                return new PasteResult(PasteOutcome.WrongWindow, thief);
            }
        }

        if (string.Equals(method, "None", StringComparison.OrdinalIgnoreCase))
        {
            SendAutoSubmit(settings.AutoSubmitKey);
            return new PasteResult(PasteOutcome.Delivered);
        }

        // Windows Terminal hands each injected key to the shell app as its own
        // input event, so a TUI that redraws per key (agy, claude) makes a Direct
        // transcript trickle in for seconds. One Ctrl+V arrives as a single paste.
        if (string.Equals(method, "Direct", StringComparison.OrdinalIgnoreCase)
            && _bridge.IsWindowsTerminalForeground())
        {
            Log.Info("Paste: Windows Terminal foreground; using CtrlV instead of Direct.");
            method = "CtrlV";
        }

        if (string.Equals(method, "Direct", StringComparison.OrdinalIgnoreCase))
        {
            var citrix = _bridge.IsCitrixForeground();
            var delay = citrix ? settings.DirectCharDelayMsCitrix : settings.DirectCharDelayMs;
            var sent = SendUnicodeString(text, delay, actualHwnd, policy, out var charsDone, out var focusHeld);
            var directErr = _bridge.GetLastWin32Error();
            var expected = (uint)(charsDone * 2);
            var outcome = PasteTargetPolicy.Classify(sent, expected, directErr, focusHeld);

            Log.Info($"Paste: Direct SendInput events injected={sent} (expected={text.Length * 2}), " +
                     $"chars={charsDone}/{text.Length}, lastErr={directErr}, citrix={citrix}, charDelayMs={delay}");

            if (!outcome.IsDelivered())
                return new PasteResult(outcome, DescribeFailure(outcome, actualHwnd, directErr), charsDone, citrix);

            SendAutoSubmit(settings.AutoSubmitKey);
            return new PasteResult(PasteOutcome.Delivered, null, charsDone, citrix);
        }

        // Clipboard-based paste: snapshot the original clipboard (if user wants it preserved),
        // replace with our text, paste, then optionally restore.
        var restoreAfter = string.Equals(settings.ClipboardHandling, "DontModify",
                                         StringComparison.OrdinalIgnoreCase);
        var previous = restoreAfter ? _bridge.TryReadClipboard() : null;

        if (!_bridge.TrySetClipboard(text))
        {
            Log.Warn("Clipboard set failed; falling back to direct keystroke injection.");
            var citrix = _bridge.IsCitrixForeground();
            var fallbackSent = SendUnicodeString(
                text, citrix ? settings.DirectCharDelayMsCitrix : settings.DirectCharDelayMs,
                actualHwnd, policy, out var fallbackChars, out var fallbackFocusHeld);
            var fallbackErr = _bridge.GetLastWin32Error();
            var fallbackOutcome = PasteTargetPolicy.Classify(
                fallbackSent, (uint)(fallbackChars * 2), fallbackErr, fallbackFocusHeld);
            if (!fallbackOutcome.IsDelivered())
                return new PasteResult(fallbackOutcome, DescribeFailure(fallbackOutcome, actualHwnd, fallbackErr), fallbackChars);

            SendAutoSubmit(settings.AutoSubmitKey);
            return new PasteResult(PasteOutcome.Delivered, null, fallbackChars);
        }
        Log.Info($"Paste: clipboard set ({text.Length} chars), method={method}, restoreAfter={restoreAfter}");

        if (settings.PasteDelayMs > 0)
            _bridge.Sleep(settings.PasteDelayMs);

        // The chord methods are a fixed, small number of events. Capture what we
        // asked for so a refusal is detectable rather than assumed successful.
        uint expectedChord;
        uint injected;
        switch (method.ToLowerInvariant())
        {
            case "shiftinsert": injected = SendShiftInsert();  expectedChord = 4; break;
            case "ctrlshiftv":  injected = SendCtrlShiftV();   expectedChord = 6; break;
            default:            injected = SendCtrlV();        expectedChord = 4; break;
        }
        var lastErr = _bridge.GetLastWin32Error();
        var chordOutcome = PasteTargetPolicy.Classify(injected, expectedChord, lastErr);
        Log.Info($"Paste: SendInput events injected={injected} (expected={expectedChord}), lastErr={lastErr}");

        // Restore the clipboard before returning on either path, or a failed
        // paste would silently leave the user's previous clipboard destroyed.
        // On failure we deliberately leave OUR text on the clipboard instead —
        // that is the recovery route the caller tells the user about.
        if (!chordOutcome.IsDelivered())
        {
            Log.Warn($"Paste: chord injection not accepted ({chordOutcome}); transcript left on the clipboard.");
            return new PasteResult(chordOutcome, DescribeFailure(chordOutcome, actualHwnd, lastErr));
        }

        SendAutoSubmit(settings.AutoSubmitKey);

        if (restoreAfter && previous is not null)
        {
            // Give the paste a moment to complete before we clobber the clipboard.
            _bridge.Sleep(Math.Max(30, settings.PasteDelayMs));
            _bridge.TrySetClipboard(previous);
        }

        return new PasteResult(PasteOutcome.Delivered);
    }

    private bool TryRestoreFocus(IntPtr targetHwnd)
    {
        if (targetHwnd == IntPtr.Zero) return false;
        try
        {
            if (!_bridge.SetForegroundWindow(targetHwnd))
                return false;
            _bridge.Sleep(50);
            return _bridge.GetForegroundWindow() == targetHwnd;
        }
        catch (Exception ex)
        {
            Log.Info($"Paste: SetForegroundWindow failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Turns a failure into something worth showing a human. An elevated target
    /// is the common, fixable case and deserves to be named explicitly rather
    /// than surfacing as a bare error number.
    /// </summary>
    private string DescribeFailure(PasteOutcome outcome, IntPtr target, int lastErr)
    {
        if (outcome == PasteOutcome.Interrupted)
            return $"focus moved to {_bridge.DescribeForegroundWindow()} during injection";

        if (outcome is PasteOutcome.Refused or PasteOutcome.Partial
            && _bridge.IsElevationMismatch(target, out var targetName, out var _))
        {
            return $"{targetName} is running as administrator and Handy is not, " +
                   $"so Windows blocked the keystrokes (lastErr={lastErr})";
        }

        return $"Windows did not accept the keystrokes (lastErr={lastErr})";
    }

    private uint SendCtrlV()
    {
        const ushort VK_CONTROL = 0x11, VK_V = 0x56;
        uint sent = 0;
        sent += _bridge.SendKey(VK_CONTROL, true);
        sent += _bridge.SendKey(VK_V, true);
        sent += _bridge.SendKey(VK_V, false);
        _bridge.Sleep(ChordHoldMs);
        sent += _bridge.SendKey(VK_CONTROL, false);
        return sent;
    }

    private uint SendShiftInsert()
    {
        const ushort VK_SHIFT = 0x10, VK_INSERT = 0x2D;
        uint sent = 0;
        sent += _bridge.SendKey(VK_SHIFT, true);
        sent += _bridge.SendKey(VK_INSERT, true);
        sent += _bridge.SendKey(VK_INSERT, false);
        _bridge.Sleep(ChordHoldMs);
        sent += _bridge.SendKey(VK_SHIFT, false);
        return sent;
    }

    private uint SendCtrlShiftV()
    {
        const ushort VK_CONTROL = 0x11, VK_SHIFT = 0x10, VK_V = 0x56;
        uint sent = 0;
        sent += _bridge.SendKey(VK_CONTROL, true);
        sent += _bridge.SendKey(VK_SHIFT,   true);
        sent += _bridge.SendKey(VK_V, true);
        sent += _bridge.SendKey(VK_V, false);
        _bridge.Sleep(ChordHoldMs);
        sent += _bridge.SendKey(VK_SHIFT,   false);
        sent += _bridge.SendKey(VK_CONTROL, false);
        return sent;
    }

    /// <summary>
    /// Types the transcript one character at a time, abandoning the attempt if
    /// focus leaves <paramref name="expectedHwnd"/> part-way through.
    ///
    /// Without that check a long transcript is split between two windows: the
    /// user gets half in their editor and half in whatever stole focus, with no
    /// indication anything went wrong. The loop can run for a second or more
    /// (length x charDelayMs), so the exposure is real, not theoretical.
    /// </summary>
    private uint SendUnicodeString(
        string text, int charDelayMs, IntPtr expectedHwnd, PasteFocusPolicy focusPolicy,
        out int charsSent, out bool focusHeld)
    {
        uint total = 0;
        charsSent = 0;
        focusHeld = true;

        for (var i = 0; i < text.Length; i++)
        {
            if (expectedHwnd != IntPtr.Zero && i > 0 && i % FocusCheckEveryChars == 0)
            {
                var current = _bridge.GetForegroundWindow();
                if (current != expectedHwnd)
                {
                    if (focusPolicy == PasteFocusPolicy.PasteAnyway)
                    {
                        // User opted to type anyway regardless of focus changes
                    }
                    else if (focusPolicy == PasteFocusPolicy.RestoreAndPaste && TryRestoreFocus(expectedHwnd))
                    {
                        Log.Info($"Paste: focus steal recovered during injection; restored 0x{expectedHwnd.ToInt64():X}.");
                    }
                    else
                    {
                        focusHeld = false;
                        Log.Warn($"Paste: focus left the target after {charsSent}/{text.Length} chars " +
                                 $"(now {_bridge.DescribeWindow(current)}); stopping injection.");
                        break;
                    }
                }
            }

            total += _bridge.SendUnicode(text[i]);
            charsSent++;
            if (charDelayMs > 0) _bridge.Sleep(charDelayMs);
        }
        return total;
    }

    private void SendAutoSubmit(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || string.Equals(key, "None", StringComparison.OrdinalIgnoreCase))
            return;

        const ushort VK_RETURN = 0x0D, VK_CONTROL = 0x11;
        switch (key.ToLowerInvariant())
        {
            case "enter":
                _bridge.SendKey(VK_RETURN, true);
                _bridge.SendKey(VK_RETURN, false);
                break;
            case "ctrlenter":
            case "cmdenter":
                _bridge.SendKey(VK_CONTROL, true);
                _bridge.SendKey(VK_RETURN,  true);
                _bridge.SendKey(VK_RETURN,  false);
                _bridge.SendKey(VK_CONTROL, false);
                break;
        }
    }

    internal static NativeMethods.INPUT Key(ushort vk, bool down) => new()
    {
        type = NativeMethods.INPUT_KEYBOARD,
        U = new NativeMethods.InputUnion
        {
            ki = new NativeMethods.KEYBDINPUT
            {
                wVk = vk,
                // Windows Terminal and several other apps gate on the scan
                // code being populated — they drop synthesised Ctrl+V if
                // wScan is 0. MapVirtualKey fills in the real scan code.
                wScan = (ushort)NativeMethods.MapVirtualKey(vk, NativeMethods.MAPVK_VK_TO_VSC),
                dwFlags = down ? 0u : NativeMethods.KEYEVENTF_KEYUP,
                time = 0,
                dwExtraInfo = IntPtr.Zero,
            }
        }
    };

    internal static NativeMethods.INPUT Unicode(char ch, bool down) => new()
    {
        type = NativeMethods.INPUT_KEYBOARD,
        U = new NativeMethods.InputUnion
        {
            ki = new NativeMethods.KEYBDINPUT
            {
                wVk = 0,
                wScan = ch,
                dwFlags = NativeMethods.KEYEVENTF_UNICODE | (down ? 0u : NativeMethods.KEYEVENTF_KEYUP),
                time = 0,
                dwExtraInfo = IntPtr.Zero,
            }
        }
    };
}
