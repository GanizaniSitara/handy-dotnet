using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using Handy.PInvoke;

namespace Handy.Services;

/// <summary>
/// Writes a transcript into the foreground app using the method configured in
/// AppSettings. Mirrors upstream's PasteMethod + ClipboardHandling + AutoSubmitKey.
/// </summary>
public sealed class TextInjectionService
{
    public void Paste(string text, AppSettings settings)
    {
        if (string.IsNullOrEmpty(text)) return;

        var method = (settings.PasteMethod ?? "CtrlV").Trim();
        if (string.Equals(method, "None", StringComparison.OrdinalIgnoreCase))
        {
            SendAutoSubmit(settings.AutoSubmitKey);
            return;
        }

        if (string.Equals(method, "Direct", StringComparison.OrdinalIgnoreCase))
        {
            SendUnicodeString(text, settings.DirectCharDelayMs);
            SendAutoSubmit(settings.AutoSubmitKey);
            return;
        }

        // Clipboard-based paste: snapshot the original clipboard (if user wants it preserved),
        // replace with our text, paste, then optionally restore.
        var restoreAfter = string.Equals(settings.ClipboardHandling, "DontModify",
                                         StringComparison.OrdinalIgnoreCase);
        var previous = restoreAfter ? TryReadClipboard() : null;

        if (!TrySetClipboard(text))
        {
            Log.Warn("Clipboard set failed; falling back to direct keystroke injection.");
            SendUnicodeString(text, settings.DirectCharDelayMs);
            SendAutoSubmit(settings.AutoSubmitKey);
            return;
        }
        Log.Info($"Paste: clipboard set ({text.Length} chars), method={method}, restoreAfter={restoreAfter}");

        if (settings.PasteDelayMs > 0)
            Thread.Sleep(settings.PasteDelayMs);

        uint injected = method.ToLowerInvariant() switch
        {
            "shiftinsert" => SendShiftInsert(),
            "ctrlshiftv"  => SendCtrlShiftV(),
            _             => SendCtrlV(),
        };
        var lastErr = Marshal.GetLastWin32Error();
        Log.Info($"Paste: SendInput events injected={injected}, lastErr={lastErr}");

        SendAutoSubmit(settings.AutoSubmitKey);

        if (restoreAfter && previous is not null)
        {
            // Give the paste a moment to complete before we clobber the clipboard.
            Thread.Sleep(Math.Max(30, settings.PasteDelayMs));
            TrySetClipboard(previous);
        }
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

    private static void SendUnicodeString(string text, int charDelayMs)
    {
        Span<NativeMethods.INPUT> pair = stackalloc NativeMethods.INPUT[2];
        foreach (var ch in text)
        {
            pair[0] = Unicode(ch, true);
            pair[1] = Unicode(ch, false);
            NativeMethods.SendInput((uint)pair.Length, ref pair[0], NativeMethods.INPUT.Size);
            if (charDelayMs > 0) Thread.Sleep(charDelayMs);
        }
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
