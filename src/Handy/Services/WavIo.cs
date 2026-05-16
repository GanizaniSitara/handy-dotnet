using System;
using System.IO;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Handy.Services;

/// <summary>
/// Minimal WAV loader for test/debug. Converts whatever format the file is in
/// (any sample rate, any channel count, PCM or IEEE float) to 16 kHz mono
/// float32 — the input Whisper expects.
/// </summary>
internal static class WavIo
{
    private const int SampleRate = 16000;

    public static float[] ReadMonoFloat16k(string path)
    {
        using var reader = new AudioFileReader(path);

        ISampleProvider source = reader;
        if (reader.WaveFormat.Channels != 1)
            source = source.ToMono();

        if (source.WaveFormat.SampleRate != 16000)
            source = new WdlResamplingSampleProvider(source, 16000);

        // Read in ~1-second chunks and copy into a growing list.
        var buf = new float[16000];
        var all = new System.Collections.Generic.List<float>(capacity: 16000 * 15);
        int read;
        while ((read = source.Read(buf, 0, buf.Length)) > 0)
        {
            for (int i = 0; i < read; i++) all.Add(buf[i]);
        }
        return all.ToArray();
    }

    public static void WriteMonoFloat16k(string path, float[] samples)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        using var writer = new WaveFileWriter(path, new WaveFormat(SampleRate, 16, 1));
        var bytes = new byte[Math.Max(0, samples.Length) * 2];
        for (int i = 0, j = 0; i < samples.Length; i++, j += 2)
        {
            var clamped = Math.Clamp(samples[i], -1f, 1f);
            var value = (short)Math.Round(clamped * short.MaxValue);
            bytes[j] = (byte)(value & 0xFF);
            bytes[j + 1] = (byte)((value >> 8) & 0xFF);
        }
        writer.Write(bytes, 0, bytes.Length);
    }
}
