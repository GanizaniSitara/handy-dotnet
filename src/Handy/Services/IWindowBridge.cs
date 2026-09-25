using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using Handy.PInvoke;

namespace Handy.Services;

public interface IWindowBridge
{
    IntPtr GetForegroundWindow();
    bool SetForegroundWindow(IntPtr hWnd);
    string DescribeWindow(IntPtr hwnd);
    string DescribeForegroundWindow();
    bool IsCitrixForeground();
    bool IsElevationMismatch(IntPtr hwnd, out string targetName, out int lastErr);
    uint SendUnicode(char ch);
    uint SendKey(ushort vk, bool down);
    bool TrySetClipboard(string text);
    string? TryReadClipboard();
    void Sleep(int ms);
    int GetLastWin32Error();
}

public sealed class DefaultWindowBridge : IWindowBridge
{
    private static readonly string[] CitrixProcessNames =
    {
        "cdviewer", "wfica32", "receiver", "selfservice",
        "citrixworkspaceapp", "workspace",
    };

    public IntPtr GetForegroundWindow() => NativeMethods.GetForegroundWindow();

    public bool SetForegroundWindow(IntPtr hWnd) => NativeMethods.SetForegroundWindow(hWnd);

    public string DescribeForegroundWindow() => DescribeWindow(GetForegroundWindow());

    public string DescribeWindow(IntPtr hwnd)
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

    public bool IsCitrixForeground()
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

    public bool IsElevationMismatch(IntPtr hwnd, out string targetName, out int lastErr)
    {
        lastErr = Marshal.GetLastWin32Error();
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

    public uint SendUnicode(char ch)
    {
        Span<NativeMethods.INPUT> pair = stackalloc NativeMethods.INPUT[2];
        pair[0] = TextInjectionService.Unicode(ch, true);
        pair[1] = TextInjectionService.Unicode(ch, false);
        return NativeMethods.SendInput(2, ref pair[0], NativeMethods.INPUT.Size);
    }

    public uint SendKey(ushort vk, bool down)
    {
        Span<NativeMethods.INPUT> i = stackalloc NativeMethods.INPUT[1];
        i[0] = TextInjectionService.Key(vk, down);
        return NativeMethods.SendInput(1, ref i[0], NativeMethods.INPUT.Size);
    }

    public bool TrySetClipboard(string text)
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

    public string? TryReadClipboard()
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

    public void Sleep(int ms)
    {
        if (ms > 0) Thread.Sleep(ms);
    }

    public int GetLastWin32Error() => Marshal.GetLastWin32Error();
}
