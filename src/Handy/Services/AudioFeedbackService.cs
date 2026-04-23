using System;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Handy.Services;

/// <summary>
/// Short synthesised beeps for recording start/stop/cancel. No sound files to
/// bundle. Tones chosen to feel like the upstream theme: rising (start),
/// falling (stop), flat low (cancel). Each event is independently toggleable
/// via settings — all default to off so a fresh install is silent.
/// </summary>
public sealed class AudioFeedbackService : IDisposable
{
    private WaveOutEvent? _out;
    private readonly object _lock = new();
    private bool _beepStart;
    private bool _beepStop;
    private bool _beepCancel;
    private float _volume;

    public AudioFeedbackService(AppSettings s) => UpdateSettings(s);

    public void UpdateSettings(AppSettings s)
    {
        _beepStart  = s.BeepOnStart;
        _beepStop   = s.BeepOnStop;
        _beepCancel = s.BeepOnCancel;
        _volume     = (float)Math.Clamp(s.BeepVolume, 0.0, 1.0);
    }

    public void PlayStart()  { if (_beepStart)  Play(frequencyHz: 880, durationMs: 90); }
    public void PlayStop()   { if (_beepStop)   Play(frequencyHz: 660, durationMs: 90); }
    public void PlayCancel() { if (_beepCancel) Play(frequencyHz: 220, durationMs: 140); }

    private void Play(double frequencyHz, int durationMs)
    {
        try
        {
            lock (_lock)
            {
                _out?.Stop();
                _out?.Dispose();

                var gen = new SignalGenerator(sampleRate: 44100, channel: 1)
                {
                    Gain = _volume,
                    Frequency = frequencyHz,
                    Type = SignalGeneratorType.Sin,
                };
                var provider = gen.Take(TimeSpan.FromMilliseconds(durationMs));
                _out = new WaveOutEvent { DesiredLatency = 80 };
                _out.Init(provider);
                _out.Play();
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"AudioFeedback: {ex.Message}");
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _out?.Dispose();
            _out = null;
        }
    }
}
