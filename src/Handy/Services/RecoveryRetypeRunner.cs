using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Handy.Services;

public enum RecoveryRetypeOutcome { Completed, Busy, ModifiersHeld }

/// <summary>Serializes recovery typing and refuses to type through held modifiers.</summary>
public sealed class RecoveryRetypeRunner
{
    private int _busy;

    public async Task<RecoveryRetypeOutcome> RunAsync(
        Func<bool> modifiersDown, Action inject, TimeSpan? releaseTimeout = null)
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
            return RecoveryRetypeOutcome.Busy;
        try
        {
            return await Task.Run(() =>
            {
                var timeout = releaseTimeout ?? TimeSpan.FromSeconds(3);
                var clock = Stopwatch.StartNew();
                while (modifiersDown())
                {
                    if (clock.Elapsed >= timeout) return RecoveryRetypeOutcome.ModifiersHeld;
                    Thread.Sleep(20);
                }
                Thread.Sleep(50);
                // The user may have started another chord during the settling delay.
                if (modifiersDown()) return RecoveryRetypeOutcome.ModifiersHeld;
                inject();
                return RecoveryRetypeOutcome.Completed;
            }).ConfigureAwait(false);
        }
        finally { Interlocked.Exchange(ref _busy, 0); }
    }
}
