using System;
using System.IO;
using System.Text.Json;
using System.Diagnostics;
using Handy.Services;

if (args.Length == 3 && args[0] == "--download-gpu-model")
{
    // --download-gpu-model <models-root> V2|V3 : the same call Settings makes.
    var variant = args[2] == "V3" ? ModelDownloadService.Variant.V3 : ModelDownloadService.Variant.V2;
    var last = DateTime.MinValue;
    var gpuDir = await ModelDownloadService.DownloadGpuAsync(args[1], variant,
        new Progress<ModelDownloadService.Progress>(p =>
        {
            if ((DateTime.UtcNow - last).TotalSeconds < 10 && p.Phase != "Done") return;
            last = DateTime.UtcNow;
            Console.WriteLine($"{p.Phase} {p.BytesSoFar / 1048576} MB");
        }), default);
    Console.WriteLine($"installed {gpuDir} fp32={ParakeetTranscriptionService.HasFp32Models(gpuDir)}");
    return;
}

if (args.Length >= 4 && args[0] == "--bench-provider")
{
    // --bench-provider <model-dir> <wav> CPU|GPU [passes] [gpu-adapter]
    await PerformanceChecks.BenchProvider(args[1], args[2], args[3],
        args.Length > 4 ? int.Parse(args[4]) : 4, args.Length > 5 ? int.Parse(args[5]) : 0);
    return;
}

if (args.Length == 3 && args[0] == "--bench-encoder")
{
    await PerformanceChecks.BenchEncoder(args[1], args[2]);
    return;
}

if (args.Length is 3 or 4 && args[0] == "--bench-priority")
{
    await PerformanceChecks.BenchPriority(args[1], args[2], args.Length == 4 ? int.Parse(args[3]) : 4);
    return;
}

if (args.Length is 3 or 4 && args[0] == "--bench-threads")
{
    await PerformanceChecks.BenchThreads(args[1], args[2], args.Length == 4 ? args[3] : "contention");
    return;
}

if (args.Length == 2 && args[0] == "--probe-paste-target")
{
    // Read-only integration check: no clipboard, focus or input changes.
    var hwnd = new IntPtr(Convert.ToInt64(args[1], 16));
    var bridge = new DefaultWindowBridge();
    for (int pass = 0; pass < 5; pass++)
    {
        var timer = Stopwatch.StartNew();
        await bridge.PreparePasteTargetAsync(hwnd);
        var probeMs = timer.Elapsed.TotalMilliseconds;
        timer.Restart();
        var method = bridge.PreferredClipboardMethod(hwnd);
        Console.WriteLine(JsonSerializer.Serialize(new { pass, hwnd = args[1], method, probeMs,
            validationMs = timer.Elapsed.TotalMilliseconds,
            otherWindowMethod = bridge.PreferredClipboardMethod(new IntPtr(1)) }));
    }
    return;
}

if (args.Length == 5 && args[0] == "--bench-model-cache")
{
    await PerformanceChecks.BenchCache(args[1], args[2], args[3], args[4]);
    return;
}
if (args.Length == 4 && args[0] == "--bench-vad-tail")
{
    await PerformanceChecks.BenchVad(args[1], args[2], args[3]);
    return;
}

if (args.Length == 4 && args[0] == "--bench-warmup")
{
    await WarmupChecks.Bench(args[1], args[2], args[3]);
    return;
}

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
    await RetypeChecks.Run();
    await CaptureAdmissionChecks.Run();
    await WarmupChecks.Run();
    await PerformanceChecks.Run();
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
