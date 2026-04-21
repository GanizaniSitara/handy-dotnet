using System;
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
            SendUnicodeString(text);
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
            SendUnicodeString(text);
            SendAutoSubmit(settings.AutoSubmitKey);
            return;
        }

        if (settings.PasteDelayMs > 0)
            Thread.Sleep(settings.PasteDelayMs);

        switch (method.ToLowerInvariant())
        {
            case "shiftinsert":  SendShiftInsert();    break;
            case "ctrlshiftv":   SendCtrlShiftV();     break;
            default:             SendCtrlV();          break;
        }

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

    private static void SendCtrlV()
    {
        const ushort VK_CONTROL = 0x11, VK_V = 0x56;
        Span<NativeMethods.INPUT> i = stackalloc NativeMethods.INPUT[4];
        i[0] = Key(VK_CONTROL, true);
        i[1] = Key(VK_V,       true);
        i[2] = Key(VK_V,       false);
        i[3] = Key(VK_CONTROL, false);
        NativeMethods.SendInput((uint)i.Length, ref i[0], NativeMethods.INPUT.Size);
    }

    private static void SendCtrlShiftV()
    {
        const ushort VK_CONTROL = 0x11, VK_SHIFT = 0x10, VK_V = 0x56;
        Span<NativeMethods.INPUT> i = stackalloc NativeMethods.INPUT[6];
        i[0] = Key(VK_CONTROL, true);
        i[1] = Key(VK_SHIFT,   true);
        i[2] = Key(VK_V,       true);
        i[3] = Key(VK_V,       false);
        i[4] = Key(VK_SHIFT,   false);
        i[5] = Key(VK_CONTROL, false);
        NativeMethods.SendInput((uint)i.Length, ref i[0], NativeMethods.INPUT.Size);
    }

    private static void SendShiftInsert()
    {
        const ushort VK_SHIFT = 0x10, VK_INSERT = 0x2D;
        Span<NativeMethods.INPUT> i = stackalloc NativeMethods.INPUT[4];
        i[0] = Key(VK_SHIFT,  true);
        i[1] = Key(VK_INSERT, true);
        i[2] = Key(VK_INSERT, false);
        i[3] = Key(VK_SHIFT,  false);
        NativeMethods.SendInput((uint)i.Length, ref i[0], NativeMethods.INPUT.Size);
    }

    private static void SendUnicodeString(string text)
    {
        Span<NativeMethods.INPUT> pair = stackalloc NativeMethods.INPUT[2];
        foreach (var ch in text)
        {
            pair[0] = Unicode(ch, true);
            pair[1] = Unicode(ch, false);
            NativeMethods.SendInput((uint)pair.Length, ref pair[0], NativeMethods.INPUT.Size);
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
                wScan = 0,
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
