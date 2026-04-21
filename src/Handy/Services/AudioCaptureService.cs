using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NAudio.Wave;

namespace Handy.Services;

/// <summary>
/// Continuous 16 kHz / mono / 16-bit mic capture with a ring buffer, so a
/// recording can pull audio from BEFORE the hotkey press (pre-roll) and a
/// trailing tail AFTER release (post-roll). Mirrors upstream's pre-roll buffer.
///
/// The WaveInEvent runs from Initialize() until Dispose(). Every block is
/// appended to a byte ring sized for the largest pre-roll a user might choose.
/// Start() latches a start position backdated by PreRollMs; StopAsync() sleeps
/// PostRollMs then latches the end position, and returns the slice as float32.
/// </summary>
public sealed class AudioCaptureService : IDisposable
{
    private const int SampleRate  = 16000;
    private const int BytesPerSec = SampleRate * 2;
    private const int RingSeconds = 3;   // covers any reasonable pre-roll + slack

    private readonly object _lock = new();
    private readonly byte[] _ring = new byte[BytesPerSec * RingSeconds];
    private long _writePos;              // absolute byte count
    private WaveInEvent? _wave;

    private bool _recording;
    private long _recStart = -1;

    private string _preferredDevice;

    public AudioCaptureService(string preferredDevice = "")
    {
        _preferredDevice = preferredDevice ?? string.Empty;
    }

    public void Initialize() => RestartCapture();

    public void SetPreferredDevice(string name)
    {
        if (string.Equals(_preferredDevice, name, StringComparison.OrdinalIgnoreCase)) return;
        _preferredDevice = name ?? string.Empty;
        RestartCapture();
    }

    public static IReadOnlyList<string> EnumerateInputDevices()
    {
        var names = new List<string>();
        for (int i = 0; i < WaveInEvent.DeviceCount; i++)
            names.Add(WaveInEvent.GetCapabilities(i).ProductName);
        return names;
    }

    /// <summary>
    /// Mark the start of a recording. The returned samples will include the
    /// last <paramref name="preRollMs"/> milliseconds captured BEFORE this call.
    /// </summary>
    public void Start(int preRollMs)
    {
        lock (_lock)
        {
            var preRollBytes = Math.Clamp(preRollMs, 0, RingSeconds * 1000) * BytesPerSec / 1000;
            _recStart = Math.Max(0, _writePos - preRollBytes);
            _recording = true;
        }
    }

    /// <summary>
    /// Stop recording. Waits <paramref name="postRollMs"/> before latching the
    /// end position, so the speaker's trailing words still land.
    /// </summary>
    public async Task<float[]> StopAsync(int postRollMs)
    {
        if (postRollMs > 0) await Task.Delay(postRollMs).ConfigureAwait(false);

        byte[] pcm;
        lock (_lock)
        {
            if (!_recording || _recStart < 0) return Array.Empty<float>();
            var recEnd = _writePos;
            _recording = false;
            pcm = ExtractUnlocked(_recStart, recEnd);
            _recStart = -1;
        }

        var samples = new float[pcm.Length / 2];
        for (int i = 0, j = 0; i < pcm.Length - 1; i += 2, j++)
        {
            short s = (short)(pcm[i] | (pcm[i + 1] << 8));
            samples[j] = s / 32768f;
        }
        return samples;
    }

    private void RestartCapture()
    {
        WaveInEvent? old;
        lock (_lock)
        {
            old = _wave;
            _wave = null;
        }
        try { old?.StopRecording(); } catch { }
        try { old?.Dispose(); } catch { }

        var device = ResolveDeviceIndex(_preferredDevice);
        var w = new WaveInEvent
        {
            DeviceNumber = device,
            WaveFormat = new WaveFormat(SampleRate, 16, 1),
            BufferMilliseconds = 50,
        };
        w.DataAvailable += OnDataAvailable;
        w.RecordingStopped += OnRecordingStopped;

        try
        {
            w.StartRecording();
            lock (_lock) { _wave = w; }
            Log.Info($"Audio capture started on device #{device} ('{WaveInEvent.GetCapabilities(device).ProductName}')");
        }
        catch (Exception ex)
        {
            Log.Error($"Audio capture failed to start: {ex.Message}");
            w.Dispose();
        }
    }

    private static int ResolveDeviceIndex(string preferred)
    {
        if (WaveInEvent.DeviceCount == 0) return 0;
        if (string.IsNullOrWhiteSpace(preferred)) return 0;
        for (int i = 0; i < WaveInEvent.DeviceCount; i++)
        {
            var name = WaveInEvent.GetCapabilities(i).ProductName;
            if (string.Equals(name, preferred, StringComparison.OrdinalIgnoreCase)) return i;
            if (preferred.StartsWith(name, StringComparison.OrdinalIgnoreCase)) return i;
        }
        Log.Warn($"Preferred input device '{preferred}' not found; using default.");
        return 0;
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        lock (_lock)
        {
            var ringLen = _ring.Length;
            var writeStart = (int)(_writePos % ringLen);
            var count = e.BytesRecorded;

            if (writeStart + count <= ringLen)
            {
                Buffer.BlockCopy(e.Buffer, 0, _ring, writeStart, count);
            }
            else
            {
                var first = ringLen - writeStart;
                Buffer.BlockCopy(e.Buffer, 0,     _ring, writeStart, first);
                Buffer.BlockCopy(e.Buffer, first, _ring, 0,          count - first);
            }
            _writePos += count;
        }
    }

    private byte[] ExtractUnlocked(long from, long to)
    {
        if (to <= from) return Array.Empty<byte>();
        var len = to - from;
        var ringLen = _ring.Length;

        // Drop anything older than the ring window.
        if (len > ringLen) { from = to - ringLen; len = ringLen; }

        var @out = new byte[len];
        var start = (int)(from % ringLen);
        if (start + len <= ringLen)
        {
            Buffer.BlockCopy(_ring, start, @out, 0, (int)len);
        }
        else
        {
            var first = ringLen - start;
            Buffer.BlockCopy(_ring, start, @out, 0,     first);
            Buffer.BlockCopy(_ring, 0,     @out, first, (int)len - first);
        }
        return @out;
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is not null)
            Log.Error($"NAudio recording stopped with error: {e.Exception.Message}");
    }

    public void Dispose()
    {
        WaveInEvent? w;
        lock (_lock) { w = _wave; _wave = null; }
        try { w?.StopRecording(); } catch { }
        try { w?.Dispose(); } catch { }
    }
}
