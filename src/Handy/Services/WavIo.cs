using System;
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
}
