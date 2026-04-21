using System;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Handy.Services;

/// <summary>
/// Short synthesised beeps for recording start/stop/cancel. No sound files to
/// bundle. Tones chosen to feel like the upstream theme: rising (start),
/// falling (stop), flat low (cancel).
/// </summary>
public sealed class AudioFeedbackService : IDisposable
{
    private WaveOutEvent? _out;
    private readonly object _lock = new();
    private bool _enabled;
    private float _volume;

    public AudioFeedbackService(AppSettings s)
    {
        _enabled = s.AudioFeedback;
        _volume  = (float)Math.Clamp(s.AudioFeedbackVolume, 0.0, 1.0);
    }

    public void UpdateSettings(AppSettings s)
    {
        _enabled = s.AudioFeedback;
        _volume  = (float)Math.Clamp(s.AudioFeedbackVolume, 0.0, 1.0);
    }

    public void PlayStart()  => Play(frequencyHz: 880, durationMs: 90);
    public void PlayStop()   => Play(frequencyHz: 660, durationMs: 90);
    public void PlayCancel() => Play(frequencyHz: 220, durationMs: 140);

    private void Play(double frequencyHz, int durationMs)
    {
        if (!_enabled) return;
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
