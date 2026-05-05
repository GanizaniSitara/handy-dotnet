using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using Handy.PInvoke;

namespace Handy.Services;

/// <summary>
/// Global WH_KEYBOARD_LL hook. Supports both toggle and push-to-talk modes by
/// reporting raw press/release events for the configured trigger and cancel chords.
///
/// Hooks require a message-pumping thread — we install from the WPF dispatcher.
/// </summary>
public sealed class LowLevelKeyHookService : IDisposable
{
    public event Action<bool>? OnTrigger;   // true = pressed, false = released
    public event Action?       OnCancel;
    public event Action?       OnCopyLast;

    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN    = 0x0100;
    private const int WM_KEYUP      = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP   = 0x0105;

    private const uint VK_LCONTROL = 0xA2, VK_RCONTROL = 0xA3, VK_CONTROL = 0x11;
    private const uint VK_LMENU    = 0xA4, VK_RMENU    = 0xA5, VK_MENU    = 0x12;
    private const uint VK_LSHIFT   = 0xA0, VK_RSHIFT   = 0xA1, VK_SHIFT   = 0x10;
    private const uint VK_LWIN     = 0x5B, VK_RWIN     = 0x5C;

    private readonly NativeMethods.HookProc _proc;
    private IntPtr _hook;

    private Hotkey _trigger;
    private Hotkey _cancel;
    private Hotkey _copyLast;
    private bool   _triggerActive;

    public LowLevelKeyHookService()
    {
        _proc = HookCallback;
    }

    public void Configure(Hotkey trigger, Hotkey cancel, Hotkey copyLast)
    {
        _trigger  = trigger;
        _cancel   = cancel;
        _copyLast = copyLast;
        _triggerActive = false;
        Log.Info($"Hotkey: trigger='{trigger.Display}' cancel='{cancel.Display}' copyLast='{copyLast.Display}'");
    }

    public void Install()
    {
        if (_hook != IntPtr.Zero) return;

        using var proc = Process.GetCurrentProcess();
        var module = proc.MainModule;
        var hMod = module is null ? IntPtr.Zero : NativeMethods.GetModuleHandle(module.ModuleName);

        _hook = NativeMethods.SetWindowsHookEx(WH_KEYBOARD_LL, _proc, hMod, 0);
        if (_hook == IntPtr.Zero)
            Log.Error($"SetWindowsHookEx failed, error={Marshal.GetLastWin32Error()}");
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0) return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);

        var msg = wParam.ToInt32();
        var isDown = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
        var isUp   = msg == WM_KEYUP   || msg == WM_SYSKEYUP;
        if (!isDown && !isUp) return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);

        var data = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);
        var vk = data.vkCode;

        // Skip modifier-only events — we wait for the base key and then sample modifier state.
        if (IsModifier(vk))
            return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);

        // Diagnostic: log every event matching the configured trigger VK, regardless of mod state.
        // Lets us confirm the hook is firing when the terminal has focus and whether mods/flags differ.
        if (!_trigger.IsEmpty && vk == _trigger.Vk)
        {
            try
            {
                var mods = CurrentMods();
                var fg = NativeMethods.GetForegroundWindow();
                var title = new StringBuilder(256);
                var cls   = new StringBuilder(128);
                if (fg != IntPtr.Zero)
                {
                    NativeMethods.GetWindowText(fg, title, title.Capacity);
                    NativeMethods.GetClassName(fg, cls, cls.Capacity);
                }
                uint pid = 0;
                if (fg != IntPtr.Zero) NativeMethods.GetWindowThreadProcessId(fg, out pid);
                string procName = "?";
                try { using var p = Process.GetProcessById((int)pid); procName = p.ProcessName; } catch { }

                Log.Info($"HOOK vk=0x{vk:X2} {(isDown ? "DN" : "UP")} flags=0x{data.flags:X2} " +
                         $"mods={mods} req={_trigger.Required} match={(_trigger.Required & mods) == _trigger.Required} " +
                         $"active={_triggerActive} fg=[{procName}|{cls}|{title}]");
            }
            catch (Exception ex) { Log.Error($"HOOK diag: {ex.Message}"); }
        }

        // Check cancel first (cancel is a simple single-key match; modifier-agnostic is fine).
        if (!_cancel.IsEmpty && vk == _cancel.Vk && isDown)
        {
            try { OnCancel?.Invoke(); } catch (Exception ex) { Log.Error($"OnCancel: {ex}"); }
            // Don't swallow Escape — users may want it to still cancel dialogs etc.
        }

        // Copy-last match. Checked before the trigger so chords that share the same VK
        // (e.g. Ctrl+Alt+Space trigger and Ctrl+Alt+Shift+Space copy-last) don't both fire.
        if (!_copyLast.IsEmpty && vk == _copyLast.Vk && isDown)
        {
            var modsPressed = CurrentMods();
            if ((_copyLast.Required & modsPressed) == _copyLast.Required)
            {
                DispatchCopyLast();
                return (IntPtr)1;
            }
        }

        // Trigger match.
        if (!_trigger.IsEmpty && vk == _trigger.Vk)
        {
            var modsPressed = CurrentMods();
            var allModsHeld = (_trigger.Required & modsPressed) == _trigger.Required;

            if (isDown && allModsHeld && !_triggerActive)
            {
                _triggerActive = true;
                DispatchTrigger(true);
                return (IntPtr)1; // swallow — don't leak Space/F-key into the focused app
            }
            if (isUp && _triggerActive)
            {
                _triggerActive = false;
                DispatchTrigger(false);
                return (IntPtr)1;
            }
        }

        return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private void DispatchTrigger(bool pressed)
    {
        // Marshal to the UI thread so handlers can touch WPF state safely.
        var app = Application.Current;
        if (app is null)
        {
            try { OnTrigger?.Invoke(pressed); } catch (Exception ex) { Log.Error($"OnTrigger: {ex}"); }
            return;
        }
        app.Dispatcher.BeginInvoke(new Action(() =>
        {
            try { OnTrigger?.Invoke(pressed); } catch (Exception ex) { Log.Error($"OnTrigger: {ex}"); }
        }));
    }

    private void DispatchCopyLast()
    {
        var app = Application.Current;
        if (app is null)
        {
            try { OnCopyLast?.Invoke(); } catch (Exception ex) { Log.Error($"OnCopyLast: {ex}"); }
            return;
        }
        app.Dispatcher.BeginInvoke(new Action(() =>
        {
            try { OnCopyLast?.Invoke(); } catch (Exception ex) { Log.Error($"OnCopyLast: {ex}"); }
        }));
    }

    private static Hotkey.Mods CurrentMods()
    {
        Hotkey.Mods m = 0;
        if (KeyIsDown(VK_LCONTROL) || KeyIsDown(VK_RCONTROL) || KeyIsDown(VK_CONTROL)) m |= Hotkey.Mods.Ctrl;
        if (KeyIsDown(VK_LMENU)    || KeyIsDown(VK_RMENU)    || KeyIsDown(VK_MENU))    m |= Hotkey.Mods.Alt;
        if (KeyIsDown(VK_LSHIFT)   || KeyIsDown(VK_RSHIFT)   || KeyIsDown(VK_SHIFT))   m |= Hotkey.Mods.Shift;
        if (KeyIsDown(VK_LWIN)     || KeyIsDown(VK_RWIN))                              m |= Hotkey.Mods.Win;
        return m;
    }

    private static bool KeyIsDown(uint vk) => (NativeMethods.GetAsyncKeyState((int)vk) & 0x8000) != 0;

    private static bool IsModifier(uint vk) =>
        vk == VK_LCONTROL || vk == VK_RCONTROL || vk == VK_CONTROL ||
        vk == VK_LMENU    || vk == VK_RMENU    || vk == VK_MENU    ||
        vk == VK_LSHIFT   || vk == VK_RSHIFT   || vk == VK_SHIFT   ||
        vk == VK_LWIN     || vk == VK_RWIN;

    public void Dispose()
    {
        if (_hook != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
    }
}
