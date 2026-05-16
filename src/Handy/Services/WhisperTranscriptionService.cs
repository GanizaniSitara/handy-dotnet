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
        _modelPath = ResolveExistingModelPath(whisperModelsDir, _modelName);

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

    public async Task<string> TranscribeAsync(float[] samples, TranscriptionOptions? options = null)
    {
        if (!IsReady) throw new InvalidOperationException("Whisper model not loaded.");

        var sw = Stopwatch.StartNew();
        var text = new StringBuilder();

        options ??= TranscriptionOptions.None;
        var builder = _factory!.CreateBuilder()
            .WithLanguageDetection()
            .WithNoContext();

        if (options.HasWhisperPrompt)
        {
            builder.WithPrompt(options.WhisperPrompt);
            if (options.WhisperCarryInitialPrompt)
                builder.WithCarryInitialPrompt(true);
        }

        using var processor = builder.Build();

        await foreach (var segment in processor.ProcessAsync(samples).ConfigureAwait(false))
        {
            text.Append(segment.Text);
        }

        var result = text.ToString().Trim();
        var promptDetail = options.HasWhisperPrompt
            ? $"; promptTerms={options.WhisperPromptTermCount} promptChars={options.WhisperPrompt.Length} carryPrompt={options.WhisperCarryInitialPrompt}"
            : string.Empty;
        Log.Info($"Whisper {_modelName}: {samples.Length} samples in {sw.ElapsedMilliseconds} ms{promptDetail}");
        return result;
    }

    public void Dispose() => _factory?.Dispose();

    public static string NormalizeModelName(string? modelName)
    {
        var normalized = modelName?.Trim().ToLowerInvariant().Replace('_', '.').Replace('-', '.');
        return normalized switch
        {
            "tiny.en" => "tiny.en",
            "tiny" => "tiny",
            "base.en" => "base.en",
            "small" => "small",
            "small.en" => "small.en",
            _ => "base",
        };
    }

    public static string ModelPathFor(string whisperModelsDir, string modelName)
        => Path.Combine(whisperModelsDir, $"ggml-{NormalizeModelName(modelName)}.bin");

    public static string ResolveExistingModelPath(string whisperModelsDir, string modelName)
    {
        var normalized = NormalizeModelName(modelName);
        var canonical = ModelPathFor(whisperModelsDir, normalized);
        if (File.Exists(canonical))
            return canonical;

        // Older local installs sometimes have Whisper files directly under
        // %APPDATA%\Handy\models\ instead of the newer models\whisper folder.
        var parent = Directory.GetParent(whisperModelsDir)?.FullName;
        if (!string.IsNullOrWhiteSpace(parent))
        {
            var legacy = Path.Combine(parent, $"ggml-{normalized}.bin");
            if (File.Exists(legacy))
                return legacy;
        }

        return canonical;
    }

    public static GgmlType GgmlTypeFor(string modelName)
    {
        return NormalizeModelName(modelName) switch
        {
            "tiny.en" => GgmlType.TinyEn,
            "tiny" => GgmlType.Tiny,
            "base.en" => GgmlType.BaseEn,
            "small.en" => GgmlType.SmallEn,
            "small" => GgmlType.Small,
            _ => GgmlType.Base,
        };
    }
}
