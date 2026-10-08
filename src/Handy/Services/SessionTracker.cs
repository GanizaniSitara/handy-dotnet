using System;
using System.IO;
using System.Text.Json;
using System.Threading;

namespace Handy.Services;

/// <summary>
/// Coarse phase of the dictation pipeline. Persisted with the session marker so
/// that after an abnormal death we can say *where* it died, not just that it did.
/// </summary>
public enum SessionPhase
{
    Idle,
    Listening,
    SpecPrepass,
    Asr,
    Post,
    Paste,
    Shutdown,
    Stopping,
    Vad,
    AsrWait,
    History,
    Clipboard,
}

/// <summary>State of the previous run, recovered at startup.</summary>
public sealed class PreviousSession
{
    public string SessionId { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public int Pid { get; set; }
    public DateTime StartedUtc { get; set; }
    public DateTime HeartbeatUtc { get; set; }
    public string Phase { get; set; } = nameof(SessionPhase.Idle);
    public long PhaseAgeMs { get; set; }
    public long WorkingSetBytes { get; set; }
    public long GcTotalMemoryBytes { get; set; }
}

/// <summary>
/// Crash detection by marker file. A marker is written at startup and deleted on
/// a clean exit, so a marker still present on the next launch means the previous
/// run died without exiting — the one signal that distinguishes "crashed" from
/// "closed", which the log alone has never been able to tell us.
///
/// Everything here is best-effort: diagnostics must never take the app down.
/// </summary>
public sealed class SessionTracker : IDisposable
{
    private const string FileName = "session.json";
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(30);

    // A dictation tool has no business anywhere near this. Past it, a leak is
    // starving the rest of the machine — surface that in the log within a
    // heartbeat instead of leaving it to be noticed by symptom hours later.
    private const long MemoryWarnThresholdBytes = 2L * 1024 * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _path;
    private readonly object _lock = new();
    private readonly ITimer? _heartbeat;
    private readonly TimeProvider _time;

    private readonly string _sessionId = Guid.NewGuid().ToString("N")[..8];
    private readonly string _version;
    private readonly DateTime _startedUtc = DateTime.UtcNow;

    private SessionPhase _phase = SessionPhase.Idle;
    private bool _closed;
    private bool _memoryWarned;
    private long _phaseStarted;
    private bool _phaseWarned;

    public string SessionId => _sessionId;

    public SessionTracker(string dataDir, string version, TimeProvider? timeProvider = null)
    {
        _path = Path.Combine(dataDir, FileName);
        _version = version;
        _time = timeProvider ?? TimeProvider.System;
        _phaseStarted = _time.GetTimestamp();
        _heartbeat = _time.CreateTimer(_ => Touch(), null, HeartbeatInterval, HeartbeatInterval);
    }

    /// <summary>
    /// Reads and clears any marker left by a previous run. Returns null when the
    /// previous run exited cleanly (or there was no previous run). Call before
    /// <see cref="Start"/> so the new marker doesn't overwrite the evidence.
    /// </summary>
    public PreviousSession? TakePreviousIfUnclean()
    {
        try
        {
            if (!File.Exists(_path)) return null;
            var json = File.ReadAllText(_path);
            try { File.Delete(_path); } catch { }
            return JsonSerializer.Deserialize<PreviousSession>(json, JsonOptions);
        }
        catch
        {
            // A torn or unreadable marker still means the previous run didn't
            // get to delete it, so report the death even without the detail.
            try { File.Delete(_path); } catch { }
            return new PreviousSession();
        }
    }

    public void Start() => Write();

    public void SetPhase(SessionPhase phase)
    {
        lock (_lock)
        {
            if (_closed || _phase == phase) return;
            _phase = phase;
            _phaseStarted = _time.GetTimestamp();
            _phaseWarned = false;
        }
        Write();
    }

    /// <summary>Clean shutdown: remove the marker so the next launch stays quiet.</summary>
    public void MarkCleanExit()
    {
        lock (_lock)
        {
            if (_closed) return;
            _closed = true;
        }
        try { File.Delete(_path); } catch { }
    }

    private void Touch()
    {
        lock (_lock) { if (_closed) return; }
        Write();
    }

    private void Write()
    {
        var workingSet = Environment.WorkingSet;

        PreviousSession snapshot;
        lock (_lock)
        {
            if (_closed) return;
            snapshot = new PreviousSession
            {
                SessionId          = _sessionId,
                Version            = _version,
                Pid                = Environment.ProcessId,
                StartedUtc         = _startedUtc,
                HeartbeatUtc       = DateTime.UtcNow,
                Phase              = _phase.ToString(),
                PhaseAgeMs         = (long)_time.GetElapsedTime(_phaseStarted).TotalMilliseconds,
                WorkingSetBytes    = workingSet,
                GcTotalMemoryBytes = GC.GetTotalMemory(forceFullCollection: false),
            };

            // A live timer is not proof that the dictation worker is progressing.
            // Report prolonged work once per phase; long ASR can be legitimate.
            if (!_phaseWarned && IsProlongedPhase(_phase, snapshot.PhaseAgeMs))
            {
                _phaseWarned = true;
                Log.Warn($"Session: prolonged phase id={_sessionId} phase={_phase} " +
                         $"phaseAgeMs={snapshot.PhaseAgeMs}; heartbeat alive, progress unconfirmed");
            }

            if (!_memoryWarned && workingSet > MemoryWarnThresholdBytes)
            {
                _memoryWarned = true;
                Log.Warn($"Session {_sessionId} working set is {workingSet / 1024 / 1024} MB, " +
                         $"past the {MemoryWarnThresholdBytes / 1024 / 1024} MB watchdog threshold " +
                         "— restart Handy to release it.");
            }
        }

        try
        {
            // Write-then-replace: a torn marker would be indistinguishable from
            // a crash and would cry wolf on the next launch.
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(snapshot, JsonOptions));
            File.Move(temp, _path, overwrite: true);
        }
        catch { }
    }

    public void Dispose()
    {
        _heartbeat?.Dispose();
    }

    public static bool IsProlongedPhase(SessionPhase phase, long ageMs) => phase switch
    {
        SessionPhase.Idle or SessionPhase.Listening or SessionPhase.Shutdown => false,
        SessionPhase.Asr or SessionPhase.AsrWait or SessionPhase.SpecPrepass => ageMs >= 120_000,
        _ => ageMs >= 30_000,
    };
}
