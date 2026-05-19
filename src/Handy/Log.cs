using System;
using System.IO;
using System.Threading;

namespace Handy;

internal enum LogVerbosity
{
    /// <summary>Only the text-in / text-out lines (Raw / Filter / Transcript).</summary>
    Quiet = 1,
    /// <summary>Above plus Paste / Recording / startup lines — what a user usually wants while troubleshooting.</summary>
    Normal = 2,
    /// <summary>Above plus VAD / ASR stage timings / Flow / Diag — for digging into latency or audio path.</summary>
    Verbose = 3,
    /// <summary>Above plus per-keypress HOOK lines — full firehose, expect noise.</summary>
    Debug = 4,
}

internal static class Log
{
    private const long MaxBytes = 500_000;       // matches upstream tauri-plugin-log
    private const string MutexName = @"Global\Handy.Log.v1";

    private static StreamWriter? _file;
    private static Mutex?        _writeMutex;

    private static LogVerbosity _fileVerbosity = LogVerbosity.Debug;
    private static LogVerbosity _displayVerbosity = LogVerbosity.Normal;

    public static Action<string>? Sink;

    public static void SetVerbosity(LogVerbosity file, LogVerbosity display)
    {
        _fileVerbosity = file;
        _displayVerbosity = display;
    }

    public static LogVerbosity ParseVerbosity(string? value, LogVerbosity fallback) =>
        (value ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "quiet"   => LogVerbosity.Quiet,
            "normal"  => LogVerbosity.Normal,
            "verbose" => LogVerbosity.Verbose,
            "debug"   => LogVerbosity.Debug,
            _         => fallback,
        };

    // Categorise an INFO line by its leading prefix. WARN / ERROR bypass this
    // and always pass through. Keep this list in sync with the verbosity
    // dropdowns in the Settings → Log tab.
    private static LogVerbosity CategoryFor(string msg)
    {
        if (msg.StartsWith("Raw:")        || msg.StartsWith("Filter:")    || msg.StartsWith("Transcript:"))
            return LogVerbosity.Quiet;
        if (msg.StartsWith("HOOK "))
            return LogVerbosity.Debug;
        if (msg.StartsWith("VAD:")        || msg.StartsWith("Preproc:")   || msg.StartsWith("Encoder:") ||
            msg.StartsWith("Decode:")     || msg.StartsWith("Flow:")      || msg.StartsWith("Spec:")   ||
            msg.StartsWith("Spec prefix") || msg.StartsWith("Diag:")      ||
            msg.StartsWith("Audio capture") || msg.StartsWith("Primary instance"))
            return LogVerbosity.Verbose;
        return LogVerbosity.Normal;
    }

    public static void Init(string path)
    {
        try
        {
            RotateIfNeeded(path);

            var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            _file = new StreamWriter(fs) { AutoFlush = true };

            _writeMutex = new Mutex(initiallyOwned: false, name: MutexName);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Log init failed: {ex.Message}");
        }
    }

    public static void Shutdown()
    {
        _file?.Dispose();
        _file = null;
        _writeMutex?.Dispose();
        _writeMutex = null;
    }

    public static void Info(string msg)  => Write("INFO",  msg);
    public static void Warn(string msg)  => Write("WARN",  msg);
    public static void Error(string msg) => Write("ERROR", msg);

    private static void Write(string level, string msg)
    {
        var line = $"{DateTime.Now:HH:mm:ss} {level,-5} {msg}";

        // Always-pass channels: anything not INFO (WARN / ERROR) bypasses the
        // verbosity filter so problems never get hidden by a quiet setting.
        var alwaysPass = !string.Equals(level, "INFO", StringComparison.Ordinal);
        var category = alwaysPass ? LogVerbosity.Quiet : CategoryFor(msg);

        if (_file is not null && (alwaysPass || (int)category <= (int)_fileVerbosity))
        {
            var held = false;
            try
            {
                held = _writeMutex?.WaitOne(100) ?? false;
                _file.WriteLine(line);
            }
            catch (Exception ex) { Console.Error.WriteLine($"Log write failed: {ex.Message}"); }
            finally { if (held) { try { _writeMutex!.ReleaseMutex(); } catch { } } }
        }

        if (alwaysPass || (int)category <= (int)_displayVerbosity)
            Sink?.Invoke(line);
    }

    private static void RotateIfNeeded(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists || fi.Length < MaxBytes) return;

            var rotated = path + ".1";
            if (File.Exists(rotated))
            {
                try { File.Delete(rotated); } catch { }
            }
            try { File.Move(path, rotated); }
            catch (Exception ex) { Console.Error.WriteLine($"Log rotate failed: {ex.Message}"); }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Log rotate check failed: {ex.Message}");
        }
    }
}
