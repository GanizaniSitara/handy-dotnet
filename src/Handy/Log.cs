using System;
using System.IO;
using System.Threading;

namespace Handy;

internal static class Log
{
    private const long MaxBytes = 500_000;       // matches upstream tauri-plugin-log
    private const string MutexName = @"Global\Handy.Log.v1";

    private static StreamWriter? _file;
    private static Mutex?        _writeMutex;

    public static Action<string>? Sink;

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

        if (_file is not null)
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
