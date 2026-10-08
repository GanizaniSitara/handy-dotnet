using System;
using System.Threading;
using System.Threading.Tasks;
using Handy.Services;

static class RetypeChecks
{
    public static async Task Run()
    {
        var runner = new RecoveryRetypeRunner();
        var injections = 0;
        var held = await runner.RunAsync(() => true, () => injections++, TimeSpan.Zero);
        Check(held == RecoveryRetypeOutcome.ModifiersHeld && injections == 0, "held modifiers must not inject after timeout");
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var first = runner.RunAsync(() => false, () =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new Exception("test release timed out");
            injections++;
        });
        try
        {
            Check(entered.Wait(TimeSpan.FromSeconds(5)), "first worker entered injection");
            var second = await runner.RunAsync(() => false, () => injections++);
            Check(second == RecoveryRetypeOutcome.Busy, "overlapping press must be rejected");
        }
        finally { release.Set(); }
        Check(await first == RecoveryRetypeOutcome.Completed && injections == 1, "only one injection ran");
        var checks = 0;
        var changed = await runner.RunAsync(() => ++checks > 1, () => injections++);
        Check(changed == RecoveryRetypeOutcome.ModifiersHeld && injections == 1, "modifier pressed during settling delay must abort");
        try
        {
            await runner.RunAsync(() => false, () => throw new InvalidOperationException("injection failure"));
            throw new Exception("injection failure swallowed");
        }
        catch (InvalidOperationException) { }
        Check(await runner.RunAsync(() => false, () => injections++) == RecoveryRetypeOutcome.Completed,
            "failure must release busy state");
        Console.WriteLine("Retype concurrency and modifier-release checks passed (fake injection only).");
    }
    private static void Check(bool value, string message)
    {
        if (!value) throw new Exception(message);
    }
}
