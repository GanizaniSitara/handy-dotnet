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
/// therefore be cut off part-way.
/// </summary>
public sealed record PasteResult(PasteOutcome Outcome, string? Detail = null, int DeliveredChars = 0)
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

    /// <param name="intendedHwnd">
    /// Window that had focus when the user started dictating, or 0 if unknown.
    /// Anything else on screen at paste time is not where the text belongs.
    /// </param>
    public PasteResult Paste(string text, AppSettings settings, IntPtr intendedHwnd)
    {
        if (string.IsNullOrEmpty(text)) return new PasteResult(PasteOutcome.Delivered);

        var method = (settings.PasteMethod ?? "CtrlV").Trim();
        Log.Info($"Paste: method={method}, len={text.Length}, fg={DescribeForegroundWindow()}");

        // Refuse to type into a window the user wasn't looking at. Injecting
        // anyway is how a transcript ends up in a chat window, or split across
        // two apps when the steal lands mid-injection.
        var actualHwnd = NativeMethods.GetForegroundWindow();
        if (PasteTargetPolicy.IsWrongWindow(intendedHwnd.ToInt64(), actualHwnd.ToInt64()))
        {
            var thief = DescribeWindow(actualHwnd);
            Log.Warn($"Paste: target window changed since dictation started — not typing. " +
                     $"intended=0x{intendedHwnd.ToInt64():X} actual={thief}");
            return new PasteResult(PasteOutcome.WrongWindow, thief);
        }

        if (string.Equals(method, "None", StringComparison.OrdinalIgnoreCase))
        {
            SendAutoSubmit(settings.AutoSubmitKey);
            return new PasteResult(PasteOutcome.Delivered);
        }

        if (string.Equals(method, "Direct", StringComparison.OrdinalIgnoreCase))
        {
            var citrix = IsCitrixForeground();
            var delay = citrix ? settings.DirectCharDelayMsCitrix : settings.DirectCharDelayMs;
            var sent = SendUnicodeString(text, delay, actualHwnd, out var charsDone, out var focusHeld);
            var directErr = Marshal.GetLastWin32Error();
            var expected = (uint)(charsDone * 2);
            var outcome = PasteTargetPolicy.Classify(sent, expected, directErr, focusHeld);

            Log.Info($"Paste: Direct SendInput events injected={sent} (expected={text.Length * 2}), " +
                     $"chars={charsDone}/{text.Length}, lastErr={directErr}, citrix={citrix}, charDelayMs={delay}");

            if (!outcome.IsDelivered())
                return new PasteResult(outcome, DescribeFailure(outcome, actualHwnd, directErr), charsDone);

            SendAutoSubmit(settings.AutoSubmitKey);
            return new PasteResult(PasteOutcome.Delivered, null, charsDone);
        }

        // Clipboard-based paste: snapshot the original clipboard (if user wants it preserved),
        // replace with our text, paste, then optionally restore.
        var restoreAfter = string.Equals(settings.ClipboardHandling, "DontModify",
                                         StringComparison.OrdinalIgnoreCase);
        var previous = restoreAfter ? TryReadClipboard() : null;

        if (!TrySetClipboard(text))
        {
            Log.Warn("Clipboard set failed; falling back to direct keystroke injection.");
            var citrix = IsCitrixForeground();
            var fallbackSent = SendUnicodeString(
                text, citrix ? settings.DirectCharDelayMsCitrix : settings.DirectCharDelayMs,
                actualHwnd, out var fallbackChars, out var fallbackFocusHeld);
            var fallbackErr = Marshal.GetLastWin32Error();
            var fallbackOutcome = PasteTargetPolicy.Classify(
                fallbackSent, (uint)(fallbackChars * 2), fallbackErr, fallbackFocusHeld);
            if (!fallbackOutcome.IsDelivered())
                return new PasteResult(fallbackOutcome, DescribeFailure(fallbackOutcome, actualHwnd, fallbackErr), fallbackChars);

            SendAutoSubmit(settings.AutoSubmitKey);
            return new PasteResult(PasteOutcome.Delivered, null, fallbackChars);
        }
        Log.Info($"Paste: clipboard set ({text.Length} chars), method={method}, restoreAfter={restoreAfter}");

        if (settings.PasteDelayMs > 0)
            Thread.Sleep(settings.PasteDelayMs);

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
        var lastErr = Marshal.GetLastWin32Error();
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
            Thread.Sleep(Math.Max(30, settings.PasteDelayMs));
            TrySetClipboard(previous);
        }

        return new PasteResult(PasteOutcome.Delivered);
    }

    /// <summary>
    /// Turns a failure into something worth showing a human. An elevated target
    /// is the common, fixable case and deserves to be named explicitly rather
    /// than surfacing as a bare error number.
    /// </summary>
    private static string DescribeFailure(PasteOutcome outcome, IntPtr target, int lastErr)
    {
        if (outcome == PasteOutcome.Interrupted)
            return $"focus moved to {DescribeForegroundWindow()} during injection";

        if (outcome is PasteOutcome.Refused or PasteOutcome.Partial
            && IsElevationMismatch(target, out var targetName))
        {
            return $"{targetName} is running as administrator and Handy is not, " +
                   $"so Windows blocked the keystrokes (lastErr={lastErr})";
        }

        return $"Windows did not accept the keystrokes (lastErr={lastErr})";
    }

    /// <summary>
    /// True when the target window's process is elevated and ours isn't — the
    /// UIPI case. Best-effort: any probing failure returns false rather than
    /// guessing, so we never invent an explanation.
    /// </summary>
    private static bool IsElevationMismatch(IntPtr hwnd, out string targetName)
    {
        targetName = "that window";
        try
        {
            if (hwnd == IntPtr.Zero) return false;
            NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == 0) return false;

            try
            {
                using var p = Process.GetProcessById((int)pid);
                targetName = p.ProcessName;
            }
            catch { }

            if (!TryGetElevation(pid, out var targetElevated)) return false;
            if (!TryGetElevation((uint)Environment.ProcessId, out var selfElevated)) return false;

            return targetElevated && !selfElevated;
        }
        catch { return false; }
    }

    private static bool TryGetElevation(uint pid, out bool elevated)
    {
        elevated = false;
        var process = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process == IntPtr.Zero) return false;

        var token = IntPtr.Zero;
        try
        {
            if (!NativeMethods.OpenProcessToken(process, NativeMethods.TOKEN_QUERY, out token))
                return false;
            if (!NativeMethods.GetTokenInformation(
                    token, NativeMethods.TokenElevation, out var value, sizeof(uint), out _))
                return false;
            elevated = value != 0;
            return true;
        }
        finally
        {
            if (token != IntPtr.Zero) NativeMethods.CloseHandle(token);
            NativeMethods.CloseHandle(process);
        }
    }

    private static string DescribeWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return "(none)";
        try
        {
            var title = new System.Text.StringBuilder(256);
            NativeMethods.GetWindowText(hwnd, title, title.Capacity);
            var cls = new System.Text.StringBuilder(128);
            NativeMethods.GetClassName(hwnd, cls, cls.Capacity);

            var name = string.Empty;
            NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
            if (pid != 0)
            {
                try { using var p = Process.GetProcessById((int)pid); name = p.ProcessName; } catch { }
            }
            return $"[{name}|{cls}|{title}|0x{hwnd.ToInt64():X}]";
        }
        catch (Exception ex) { return $"(err: {ex.Message})"; }
    }

    private static string? TryReadClipboard()
    {
        var app = Application.Current;
        if (app is null) return null;
        try
        {
            return app.Dispatcher.CheckAccess()
                ? (Clipboard.ContainsText() ? Clipboard.GetText() : null)
                : app.Dispatcher.Invoke(() => Clipboard.ContainsText() ? Clipboard.GetText() : null);
        }
        catch { return null; }
    }

    private static bool TrySetClipboard(string text)
    {
        var app = Application.Current;
        if (app is null) return false;

        Exception? last = null;
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (app.Dispatcher.CheckAccess())
                    Clipboard.SetText(text);
                else
                    app.Dispatcher.Invoke(() => Clipboard.SetText(text));
                return true;
            }
            catch (Exception ex)
            {
                last = ex;
                Thread.Sleep(20);
            }
        }
        Log.Warn($"Clipboard.SetText retries exhausted: {last?.Message}");
        return false;
    }

    private static void SendAutoSubmit(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || string.Equals(key, "None", StringComparison.OrdinalIgnoreCase))
            return;

        const ushort VK_RETURN = 0x0D, VK_CONTROL = 0x11;
        switch (key.ToLowerInvariant())
        {
            case "enter":
            {
                Span<NativeMethods.INPUT> i = stackalloc NativeMethods.INPUT[2];
                i[0] = Key(VK_RETURN, true);
                i[1] = Key(VK_RETURN, false);
                NativeMethods.SendInput((uint)i.Length, ref i[0], NativeMethods.INPUT.Size);
                break;
            }
            case "ctrlenter":
            case "cmdenter":
            {
                Span<NativeMethods.INPUT> i = stackalloc NativeMethods.INPUT[4];
                i[0] = Key(VK_CONTROL, true);
                i[1] = Key(VK_RETURN,  true);
                i[2] = Key(VK_RETURN,  false);
                i[3] = Key(VK_CONTROL, false);
                NativeMethods.SendInput((uint)i.Length, ref i[0], NativeMethods.INPUT.Size);
                break;
            }
        }
    }

    // Modifier chords must be sent in three stages: press the modifier(s),
    // press+release the action key, sleep, then release the modifier(s).
    // Sending a single batched 4-event SendInput collapses the Ctrl-up and
    // V-up into the same keyboard tick and Windows Terminal (plus several
    // other terminal-style apps) drops the chord. Upstream's enigo path
    // sleeps 100 ms between click and modifier release; we do the same.
    private const int ChordHoldMs = 100;

    private static uint SendOne(ushort vk, bool down)
    {
        Span<NativeMethods.INPUT> i = stackalloc NativeMethods.INPUT[1];
        i[0] = Key(vk, down);
        return NativeMethods.SendInput(1, ref i[0], NativeMethods.INPUT.Size);
    }

    private static uint SendClick(ushort vk)
    {
        Span<NativeMethods.INPUT> i = stackalloc NativeMethods.INPUT[2];
        i[0] = Key(vk, true);
        i[1] = Key(vk, false);
        return NativeMethods.SendInput(2, ref i[0], NativeMethods.INPUT.Size);
    }

    private static uint SendCtrlV()
    {
        const ushort VK_CONTROL = 0x11, VK_V = 0x56;
        uint sent = 0;
        sent += SendOne(VK_CONTROL, true);
        sent += SendClick(VK_V);
        Thread.Sleep(ChordHoldMs);
        sent += SendOne(VK_CONTROL, false);
        return sent;
    }

    private static uint SendCtrlShiftV()
    {
        const ushort VK_CONTROL = 0x11, VK_SHIFT = 0x10, VK_V = 0x56;
        uint sent = 0;
        sent += SendOne(VK_CONTROL, true);
        sent += SendOne(VK_SHIFT,   true);
        sent += SendClick(VK_V);
        Thread.Sleep(ChordHoldMs);
        sent += SendOne(VK_SHIFT,   false);
        sent += SendOne(VK_CONTROL, false);
        return sent;
    }

    private static uint SendShiftInsert()
    {
        const ushort VK_SHIFT = 0x10, VK_INSERT = 0x2D;
        uint sent = 0;
        sent += SendOne(VK_SHIFT, true);
        sent += SendClick(VK_INSERT);
        Thread.Sleep(ChordHoldMs);
        sent += SendOne(VK_SHIFT, false);
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
    private static uint SendUnicodeString(
        string text, int charDelayMs, IntPtr expectedHwnd, out int charsSent, out bool focusHeld)
    {
        Span<NativeMethods.INPUT> pair = stackalloc NativeMethods.INPUT[2];
        uint total = 0;
        charsSent = 0;
        focusHeld = true;

        for (var i = 0; i < text.Length; i++)
        {
            if (expectedHwnd != IntPtr.Zero && i > 0 && i % FocusCheckEveryChars == 0)
            {
                var current = NativeMethods.GetForegroundWindow();
                if (current != expectedHwnd)
                {
                    focusHeld = false;
                    Log.Warn($"Paste: focus left the target after {charsSent}/{text.Length} chars " +
                             $"(now {DescribeWindow(current)}); stopping injection.");
                    break;
                }
            }

            pair[0] = Unicode(text[i], true);
            pair[1] = Unicode(text[i], false);
            total += NativeMethods.SendInput((uint)pair.Length, ref pair[0], NativeMethods.INPUT.Size);
            charsSent++;
            if (charDelayMs > 0) Thread.Sleep(charDelayMs);
        }
        return total;
    }

    private static string DescribeForegroundWindow()
    {
        try
        {
            var hwnd = NativeMethods.GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return "(none)";
            var title = new System.Text.StringBuilder(256);
            NativeMethods.GetWindowText(hwnd, title, title.Capacity);
            var cls = new System.Text.StringBuilder(128);
            NativeMethods.GetClassName(hwnd, cls, cls.Capacity);
            return $"[{cls}|{title}|0x{hwnd.ToInt64():X}]";
        }
        catch (Exception ex) { return $"(err: {ex.Message})"; }
    }

    // Known Citrix client process names (case-insensitive, no .exe).
    // CDViewer = Desktop Viewer (seamless + full-desktop sessions);
    // wfica32 = legacy ICA engine; Receiver / SelfService = older client builds;
    // CitrixWorkspaceApp / Workspace = current Workspace App.
    private static readonly string[] CitrixProcessNames =
    {
        "cdviewer", "wfica32", "receiver", "selfservice",
        "citrixworkspaceapp", "workspace",
    };

    private static bool IsCitrixForeground()
    {
        try
        {
            var hwnd = NativeMethods.GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return false;
            NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == 0) return false;
            using var p = Process.GetProcessById((int)pid);
            var name = p.ProcessName;
            foreach (var candidate in CitrixProcessNames)
            {
                if (string.Equals(name, candidate, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
        catch { return false; }
    }

    private static NativeMethods.INPUT Key(ushort vk, bool down) => new()
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

    private static NativeMethods.INPUT Unicode(char ch, bool down) => new()
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
