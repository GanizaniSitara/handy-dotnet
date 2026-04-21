using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Handy.Services;

/// <summary>
/// Cross-process single-instance coordination. The first instance holds a
/// named mutex and listens on a named pipe for CLI signals from subsequent
/// invocations (mirrors upstream's tauri-plugin-single-instance behaviour).
///
/// Known signals: --toggle-transcription, --cancel, --show.
/// </summary>
public static class SingleInstance
{
    private const string MutexName = @"Global\Handy.Instance.v1";
    private const string PipeName  = "Handy.Pipe.v1";

    private static Mutex? _mutex;
    private static CancellationTokenSource? _cts;

    public static event Action<string>? OnSignal;

    /// <summary>
    /// Returns true if we're the primary instance. Otherwise forwards any
    /// recognised CLI flags to the running instance and returns false.
    /// </summary>
    public static bool AcquireOrForward(string[] args)
    {
        bool created;
        _mutex = new Mutex(initiallyOwned: true, name: MutexName, out created);
        if (created)
        {
            StartListener();
            return true;
        }

        // We're the secondary — forward any known signals.
        foreach (var signal in new[] { "--toggle-transcription", "--cancel", "--show" })
        {
            if (Array.Exists(args, a => a == signal))
            {
                TryForward(signal);
            }
        }
        return false;
    }

    private static void StartListener()
    {
        _cts = new CancellationTokenSource();
        Log.Info($"Primary instance — starting pipe listener on \\\\.\\pipe\\{PipeName}");
        _ = Task.Run(() => ListenLoopAsync(_cts.Token));
    }

    private static async Task ListenLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    PipeName, PipeDirection.In, maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

                await server.WaitForConnectionAsync(ct).ConfigureAwait(false);

                using var reader = new StreamReader(server, Encoding.UTF8);
                string? line = await reader.ReadLineAsync().ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(line))
                {
                    var signal = line.Trim();
                    Log.Info($"Received CLI signal from another instance: {signal}");
                    try { OnSignal?.Invoke(signal); }
                    catch (Exception ex) { Log.Warn($"OnSignal handler threw: {ex.Message}"); }
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                Log.Warn($"Pipe listener error: {ex.Message}");
                await Task.Delay(250, ct).ConfigureAwait(false);
            }
        }
    }

    private static void TryForward(string signal)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(timeout: 2000);
            using var writer = new StreamWriter(client, new UTF8Encoding(false)) { AutoFlush = true };
            writer.WriteLine(signal);
            Log.Info($"Forwarded '{signal}' to running instance.");
        }
        catch (Exception ex)
        {
            var msg = $"Failed to forward '{signal}' to running instance: {ex.Message}";
            Log.Warn(msg);
            Console.Error.WriteLine(msg);
        }
    }

    public static void Shutdown()
    {
        try { _cts?.Cancel(); } catch { }
        try { _mutex?.ReleaseMutex(); } catch { }
        _mutex?.Dispose();
        _mutex = null;
    }
}
