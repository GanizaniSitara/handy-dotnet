using System;
using System.Reflection;
using System.Runtime.InteropServices;
using Handy.Services;

// Exercise the real callback without installing a hook, injecting input,
// creating a WPF Application, or changing focus/clipboard contents.
static class HookChecks
{
    public static void Run()
    {
        using var hook = new LowLevelKeyHookService();
        hook.Configure(Hotkey.Parse("Ctrl+Space"), default, default,
            Hotkey.Parse("Alt+Shift+C"), Hotkey.Parse("Alt+Shift+V"), default);
        var copies = 0;
        var retypes = 0;
        hook.OnCopyLast += () => copies++;
        hook.OnRetypeLast += () => retypes++;
        var callback = typeof(LowLevelKeyHookService).GetMethod("HookCallback", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var buffer = Marshal.AllocHGlobal(Marshal.SizeOf<KeyEvent>());
        try
        {
            IntPtr Key(uint vk, bool down)
            {
                Marshal.StructureToPtr(new KeyEvent { Vk = vk }, buffer, false);
                return (IntPtr)callback.Invoke(hook, new object[] { 0, new IntPtr(down ? 0x100 : 0x101), buffer })!;
            }
            Key(0xA4, true); // left Alt
            Key(0xA0, true); // left Shift
            for (var i = 0; i < 20; i++) Key(0x43, true);
            if (copies != 1) throw new Exception($"held copy shortcut dispatched {copies} times; expected 1");
            if (Key(0x43, false) != new IntPtr(1)) throw new Exception("copy key-up leaked into target");
            Key(0x43, true);
            Key(0x43, false);
            if (copies != 2) throw new Exception("second deliberate copy press did not fire");
            for (var i = 0; i < 20; i++) Key(0x56, true);
            if (retypes != 1) throw new Exception($"held retype shortcut dispatched {retypes} times; expected 1");
            Key(0xA4, false);
            Key(0xA0, false);
            if (Key(0x56, false) != new IntPtr(1)) throw new Exception("retype key-up leaked after modifier release");
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyEvent
    {
        public uint Vk, Scan, Flags, Time;
        public IntPtr Extra;
    }
}
