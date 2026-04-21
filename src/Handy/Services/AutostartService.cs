using System;
using System.Diagnostics;
using Microsoft.Win32;

namespace Handy.Services;

/// <summary>
/// Toggle Handy autostart via the per-user Run key. Does not need admin.
///   HKCU\Software\Microsoft\Windows\CurrentVersion\Run\Handy
/// </summary>
internal static class AutostartService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Handy";

    public static void Apply(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(RunKey);
            if (key is null) return;

            if (enabled)
            {
                var exe = Process.GetCurrentProcess().MainModule?.FileName;
                if (string.IsNullOrEmpty(exe)) return;
                // Start hidden via CLI so we land straight in the tray.
                key.SetValue(ValueName, $"\"{exe}\" --start-hidden");
            }
            else
            {
                if (key.GetValue(ValueName) is not null)
                    key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"AutostartService.Apply({enabled}) failed: {ex.Message}");
        }
    }

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is not null;
        }
        catch { return false; }
    }
}
