using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Handy.Services;

/// <summary>First-touch inference outside the user's dictation, sharing its ASR gate.</summary>
public sealed class TranscriptionWarmup : IDisposable
{
    private readonly ITranscriptionService _asr;
    private readonly SemaphoreSlim _gate;
    private readonly Func<bool> _busy;
    private readonly object _sync = new();
    private Task _pending = Task.CompletedTask;
    private bool _stopped;

    public TranscriptionWarmup(ITranscriptionService asr, SemaphoreSlim gate, Func<bool> busy)
        => (_asr, _gate, _busy) = (asr, gate, busy);

    public Task RequestAsync(string reason)
    {
        lock (_sync)
        {
            if (_stopped || !_pending.IsCompleted) return _pending;
            return _pending = Task.Run(() => RunAsync(reason));
        }
    }

    private async Task RunAsync(string reason)
    {
        // Never queue behind a user's decode. That decode warms the model itself.
        if (!_gate.Wait(0)) return;
        try
        {
            lock (_sync)
                if (_stopped) return;
            if (_busy() || !_asr.IsReady) return;
            var timer = Stopwatch.StartNew();
            Log.Info($"Session: ASR warmup started reason={reason}");
            // One second of synthetic silence exercises preprocessor, encoder and
            // decoder without recording the mic. No filter/history/paste callbacks.
            _ = await _asr.TranscribeAsync(new float[16000], TranscriptionOptions.None).ConfigureAwait(false);
            Log.Info($"Session: ASR warmup completed reason={reason} elapsedMs={timer.ElapsedMilliseconds}");
        }
        catch (Exception ex)
        {
            // A failed optional warm-up must not prevent ordinary dictation.
            Log.Warn($"ASR warmup failed reason={reason}: {ex.Message}");
        }
        finally { _gate.Release(); }
    }

    public void Dispose()
    {
        Task pending;
        lock (_sync)
        {
            _stopped = true;
            pending = _pending;
        }
        // Called before releasing the model/gate. The worker never invokes the
        // UI synchronously, so this cannot deadlock the dispatcher.
        pending.GetAwaiter().GetResult();
    }
}
