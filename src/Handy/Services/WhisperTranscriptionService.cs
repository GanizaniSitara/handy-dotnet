using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Whisper.net;
using Whisper.net.Ggml;

namespace Handy.Services;

/// <summary>
/// Local Whisper transcription through Whisper.net/whisper.cpp.
/// Models are cached under %APPDATA%\Handy\models\whisper\ as GGML files.
/// </summary>
public sealed class WhisperTranscriptionService : ITranscriptionService
{
    private readonly WhisperFactory? _factory;
    private readonly string _modelPath;
    private readonly string _modelName;

    public bool IsReady => _factory is not null;

    public WhisperTranscriptionService(string whisperModelsDir, string modelName)
    {
        _modelName = NormalizeModelName(modelName);
        _modelPath = ModelPathFor(whisperModelsDir, _modelName);

        try
        {
            Directory.CreateDirectory(whisperModelsDir);
            if (!File.Exists(_modelPath))
            {
                Log.Warn($"Whisper {_modelName} model missing: {_modelPath}");
                return;
            }

            var sw = Stopwatch.StartNew();
            _factory = WhisperFactory.FromPath(_modelPath);
            Log.Info($"Loaded Whisper {_modelName} model in {sw.ElapsedMilliseconds} ms: {_modelPath}");
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to load Whisper {_modelName} model: {ex}");
        }
    }

    public async Task<string> TranscribeAsync(float[] samples)
    {
        if (!IsReady) throw new InvalidOperationException("Whisper model not loaded.");

        var sw = Stopwatch.StartNew();
        var text = new StringBuilder();

        using var processor = _factory!.CreateBuilder()
            .WithLanguageDetection()
            .WithNoContext()
            .Build();

        await foreach (var segment in processor.ProcessAsync(samples).ConfigureAwait(false))
        {
            text.Append(segment.Text);
        }

        var result = text.ToString().Trim();
        Log.Info($"Whisper {_modelName}: {samples.Length} samples in {sw.ElapsedMilliseconds} ms");
        return result;
    }

    public void Dispose() => _factory?.Dispose();

    public static string NormalizeModelName(string? modelName)
    {
        return modelName?.Trim().ToLowerInvariant() switch
        {
            "tiny" => "tiny",
            "small" => "small",
            _ => "base",
        };
    }

    public static string ModelPathFor(string whisperModelsDir, string modelName)
        => Path.Combine(whisperModelsDir, $"ggml-{NormalizeModelName(modelName)}.bin");

    public static GgmlType GgmlTypeFor(string modelName)
    {
        return NormalizeModelName(modelName) switch
        {
            "tiny" => GgmlType.Tiny,
            "small" => GgmlType.Small,
            _ => GgmlType.Base,
        };
    }
}
