using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Handy;
using Handy.Services;

var dir = Path.Combine(Path.GetTempPath(), "Handy.Diagnostics.Tests", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(dir);
var log = Path.Combine(dir, "handy.log");
try
{
    Log.SetVerbosity(LogVerbosity.Quiet, LogVerbosity.Quiet);
    Log.Sink = _ => { };
    Log.Init(log);
    var clock = new ManualTime();
    using var tracker = new SessionTracker(dir, "test", clock);
    tracker.Start();
    tracker.SetPhase(SessionPhase.Paste);
    clock.Advance(29);
    Check(!ReadLog(log).Contains("prolonged phase"), "no early warning");
    clock.Advance(1);
    Check(ReadLog(log).Contains("phase=Paste phaseAgeMs=30000"), "warning flushed at Quiet");
    tracker.SetPhase(SessionPhase.Paste);
    clock.Advance(30);
    Check(ReadLog(log).Split('\n').Count(x => x.Contains("prolonged phase")) == 1, "no repeated warning or reset on same phase");
    var marker = JsonSerializer.Deserialize<PreviousSession>(File.ReadAllText(Path.Combine(dir, "session.json")),
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    Check(marker.Phase == "Paste" && marker.PhaseAgeMs == 60000, "phase age survives heartbeat");
    tracker.SetPhase(SessionPhase.AsrWait);
    clock.Advance(119);
    Check(ReadLog(log).Split('\n').Count(x => x.Contains("prolonged phase")) == 1, "ASR wait gets longer threshold");
    clock.Advance(1);
    Check(ReadLog(log).Contains("phase=AsrWait phaseAgeMs=120000"), "ASR wait distinguished from decode");
    tracker.SetPhase(SessionPhase.Paste);
    clock.Advance(30);
    Check(ReadLog(log).Split('\n').Count(x => x.Contains("prolonged phase")) == 3, "new phase occurrence warns again");
    tracker.SetPhase(SessionPhase.Idle);
    clock.Advance(1000);
    tracker.SetPhase(SessionPhase.Listening);
    clock.Advance(1000);
    Check(ReadLog(log).Split('\n').Count(x => x.Contains("prolonged phase")) == 3, "idle and listening never warn");
    tracker.MarkCleanExit();
    clock.Advance(1000);
    Check(!File.Exists(Path.Combine(dir, "session.json")), "heartbeat after clean exit cannot recreate marker");
    Check(JsonSerializer.Deserialize<PreviousSession>("{\"Phase\":\"Asr\"}")!.PhaseAgeMs == 0, "old markers remain readable");
    Console.WriteLine("Session diagnostics checks passed (fake clock; no UI, keyboard, or clipboard access).");
}
finally
{
    Log.Shutdown();
    Directory.Delete(dir, recursive: true);
}

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static string ReadLog(string path)
{
    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
    using var reader = new StreamReader(stream);
    return reader.ReadToEnd();
}

sealed class ManualTime : TimeProvider
{
    private long _ticks;
    private TimerCallback? _callback;
    private object? _state;
    public override long TimestampFrequency => 1000;
    public override long GetTimestamp() => _ticks;
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        _callback = callback;
        _state = state;
        return new ManualTimer();
    }
    public void Advance(int seconds)
    {
        _ticks += seconds * 1000L;
        _callback?.Invoke(_state);
    }
    private sealed class ManualTimer : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => true;
        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
