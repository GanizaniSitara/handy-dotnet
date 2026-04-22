using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using NAudio.Wave;

namespace Handy.Services;

/// <summary>
/// Continuous 16 kHz / mono / 16-bit mic capture. A small byte ring backs the
/// pre-roll window (audio captured BEFORE the hotkey press); once recording
/// starts, every incoming block is also appended to an unbounded record stream
/// so utterances of any length are preserved in full.
///
/// Start() snapshots the pre-roll slice into the record stream, then keeps
/// appending live blocks until StopAsync(), which waits PostRollMs for the
/// trailing tail and returns the whole recording as float32 mono 16 kHz.
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
    private readonly MemoryStream _recBuffer = new();

    /// <summary>
    /// Fires per captured block (~50 ms) while recording. Payload is an array
    /// of 5 normalized [0,1] RMS values, one per 10 ms sub-slice, suitable
    /// for driving a small bar-style level meter in the recording overlay.
    /// </summary>
    public event Action<float[]>? OnLevels;

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
            _recBuffer.SetLength(0);
            var preRollBytes = Math.Clamp(preRollMs, 0, RingSeconds * 1000) * BytesPerSec / 1000;
            var recStart = Math.Max(0, _writePos - preRollBytes);
            var preRoll = ExtractUnlocked(recStart, _writePos);
            if (preRoll.Length > 0) _recBuffer.Write(preRoll, 0, preRoll.Length);
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
            if (!_recording) return Array.Empty<float>();
            _recording = false;
            pcm = _recBuffer.ToArray();
            _recBuffer.SetLength(0);
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
        bool recordingNow;
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

            if (_recording) _recBuffer.Write(e.Buffer, 0, count);
            recordingNow = _recording;
        }

        if (recordingNow && OnLevels is { } handler)
        {
            var levels = ComputeBarLevels(e.Buffer, e.BytesRecorded, bars: 5);
            try { handler(levels); } catch (Exception ex) { Log.Warn($"OnLevels handler threw: {ex.Message}"); }
        }
    }

    // Splits the block into N equal-length sub-slices, returns an RMS per slice
    // normalised to [0,1] via a log-ish curve so soft speech still moves bars.
    private static float[] ComputeBarLevels(byte[] pcm, int bytes, int bars)
    {
        var samples = bytes / 2;
        if (samples == 0 || bars <= 0) return new float[bars > 0 ? bars : 0];
        var perBar = Math.Max(1, samples / bars);
        var @out = new float[bars];

        for (int b = 0; b < bars; b++)
        {
            int start = b * perBar;
            int end = (b == bars - 1) ? samples : Math.Min(samples, start + perBar);
            if (end <= start) { @out[b] = 0f; continue; }

            double sumSq = 0;
            for (int i = start; i < end; i++)
            {
                short s = (short)(pcm[i * 2] | (pcm[i * 2 + 1] << 8));
                double v = s / 32768.0;
                sumSq += v * v;
            }
            var rms = Math.Sqrt(sumSq / (end - start));
            // -55 dBFS → 0, -8 dBFS → 1; same anchors upstream uses for its bars.
            var db = rms > 1e-6 ? 20.0 * Math.Log10(rms) : -80.0;
            var norm = Math.Clamp((db + 55.0) / 47.0, 0.0, 1.0);
            @out[b] = (float)Math.Pow(norm * 1.3, 0.7);
        }
        return @out;
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
        _recBuffer.Dispose();
    }
}
