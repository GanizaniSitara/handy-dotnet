using System;
using System.IO;
using System.Text.Json;
using Handy.Services;

var dir = Path.Combine(Path.GetTempPath(), "Handy.Recovery.Tests", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(dir);
try
{
    foreach (var chords in new[]
    {
        ("Ctrl+Shift+C", "Ctrl+Shift+V", "Alt+Shift+C", "Alt+Shift+V"),
        ("ctrl+shift+c", "ctrl+shift+v", "Alt+Shift+C", "Alt+Shift+V"),
        ("Ctrl+Alt+C", "Ctrl+Alt+V", "Ctrl+Alt+C", "Ctrl+Alt+V"),
        ("", "", "", ""),
        ("Alt+Shift+C", "Alt+Shift+V", "Alt+Shift+C", "Alt+Shift+V"),
    })
    {
        File.WriteAllText(Path.Combine(dir, "settings.json"), JsonSerializer.Serialize(new
        {
            settingsVersion = 4,
            copyLastHotkey = chords.Item1,
            retypeLastHotkey = chords.Item2,
        }));
        var settings = AppSettings.Load(dir);
        Check(settings.CopyLastHotkey == chords.Item3, "copy migration: " + chords.Item1);
        Check(settings.RetypeLastHotkey == chords.Item4, "retype migration: " + chords.Item2);
        var reloaded = AppSettings.Load(dir);
        Check(reloaded.CopyLastHotkey == chords.Item3 && reloaded.RetypeLastHotkey == chords.Item4,
            "migration persists and is idempotent");
    }
    HookChecks.Run();
    Console.WriteLine("Recovery shortcut migration and hook checks passed.");
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex.Message);
    Environment.ExitCode = 1;
}
finally { Directory.Delete(dir, recursive: true); }

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
