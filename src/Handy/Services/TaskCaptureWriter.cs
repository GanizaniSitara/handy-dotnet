using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Handy.PInvoke;

namespace Handy.Services;

public sealed record ForegroundWindowSnapshot(
    int Pid,
    string ProcessName,
    long Hwnd,
    string ClassName,
    string WindowTitle)
{
    public static ForegroundWindowSnapshot Capture()
    {
        var hwnd = NativeMethods.GetForegroundWindow();
        var title = new StringBuilder(512);
        var className = new StringBuilder(256);
        uint pid = 0;
        if (hwnd != IntPtr.Zero)
        {
            NativeMethods.GetWindowText(hwnd, title, title.Capacity);
            NativeMethods.GetClassName(hwnd, className, className.Capacity);
            NativeMethods.GetWindowThreadProcessId(hwnd, out pid);
        }

        var processName = string.Empty;
        try
        {
            using var process = Process.GetProcessById((int)pid);
            processName = process.ProcessName;
        }
        catch
        {
            // Foreground metadata is best-effort and must never block capture.
        }

        return new ForegroundWindowSnapshot(
            (int)pid,
            processName,
            hwnd.ToInt64(),
            className.ToString(),
            title.ToString());
    }
}

public sealed record TaskCaptureResult(string CaptureId, string Path);

public static class TaskCaptureWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
    };

    public static TaskCaptureResult Write(
        string inboxPath,
        string rawTranscript,
        string transcript,
        DateTimeOffset captureStartedAtUtc,
        ForegroundWindowSnapshot foreground)
    {
        if (string.IsNullOrWhiteSpace(inboxPath))
            throw new ArgumentException("Task capture inbox path is empty.", nameof(inboxPath));
        if (string.IsNullOrWhiteSpace(transcript))
            throw new ArgumentException("Task capture transcript is empty.", nameof(transcript));

        var inbox = Path.GetFullPath(Environment.ExpandEnvironmentVariables(inboxPath));
        Directory.CreateDirectory(inbox);

        var captureId = Guid.NewGuid().ToString();
        var capturedAt = DateTimeOffset.UtcNow;
        var version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString()
                   ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
                   ?? "unknown";
        var envelope = new
        {
            SchemaVersion = 1,
            CaptureId = captureId,
            CaptureStartedAtUtc = captureStartedAtUtc,
            CapturedAtUtc = capturedAt,
            Source = new
            {
                Application = "Handy.NET",
                Version = version,
                Host = Environment.MachineName,
            },
            Destination = "task-inbox",
            RawTranscript = rawTranscript ?? string.Empty,
            Transcript = transcript,
            Foreground = foreground,
        };

        var fileName = $"{capturedAt:yyyyMMddTHHmmssfffZ}_{captureId}.capture.json";
        var finalPath = Path.Combine(inbox, fileName);
        var temporaryPath = Path.Combine(inbox, $".{fileName}.{Guid.NewGuid():N}.tmp");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, JsonOptions);

        try
        {
            using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                options: FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.WriteByte((byte)'\n');
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, finalPath);
        }
        catch
        {
            try { File.Delete(temporaryPath); } catch { }
            throw;
        }

        return new TaskCaptureResult(captureId, finalPath);
    }
}
