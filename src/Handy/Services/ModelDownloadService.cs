using System;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Whisper.net;
using Whisper.net.Ggml;

namespace Handy.Services;

/// <summary>
/// Downloads and extracts a Parakeet model tarball from blob.handy.computer
/// into %APPDATA%\Handy\models\{name}\. Matches the archive layout upstream
/// Handy publishes (see upstream README "Manual Model Installation").
/// </summary>
public sealed class ModelDownloadService
{
    public enum Variant { V2, V3 }

    public sealed record Progress(string Phase, double Fraction, long BytesSoFar, long? TotalBytes);

    private static string UrlFor(Variant v) => v switch
    {
        Variant.V2 => "https://blob.handy.computer/parakeet-v2-int8.tar.gz",
        Variant.V3 => "https://blob.handy.computer/parakeet-v3-int8.tar.gz",
        _ => throw new ArgumentOutOfRangeException(nameof(v)),
    };

    private static string DirNameFor(Variant v) => v switch
    {
        Variant.V2 => "parakeet-tdt-0.6b-v2-int8",
        Variant.V3 => "parakeet-tdt-0.6b-v3-int8",
        _ => throw new ArgumentOutOfRangeException(nameof(v)),
    };

    /// <summary>
    /// Download + extract. Returns the full path to the model directory on success.
    /// Throws on cancellation or failure — the caller decides how to report.
    /// </summary>
    public static async Task<string> DownloadAsync(
        string modelsRoot,
        Variant variant,
        IProgress<Progress>? progress,
        CancellationToken ct)
    {
        Directory.CreateDirectory(modelsRoot);
        var targetDir = Path.Combine(modelsRoot, DirNameFor(variant));
        var tgzPath   = Path.Combine(modelsRoot, DirNameFor(variant) + ".tar.gz");

        progress?.Report(new Progress("Downloading", 0, 0, null));
        await DownloadFileAsync(UrlFor(variant), tgzPath, progress, ct).ConfigureAwait(false);

        progress?.Report(new Progress("Extracting", 0, 0, null));
        ExtractTarGz(tgzPath, modelsRoot);

        try { File.Delete(tgzPath); } catch { /* leave the archive if locked */ }

        progress?.Report(new Progress("Done", 1.0, 0, null));
        Log.Info($"Model {variant} ready at {targetDir}");
        return targetDir;
    }

    /// <summary>Directory the GPU (fp32 encoder) build of a variant lives in.</summary>
    public static string GpuDirNameFor(Variant v) => DirNameFor(v).Replace("-int8", string.Empty);

    // fp32 encoder (what a GPU wants) plus the int8 decoder, which stays on the
    // CPU and is faster there. Same NeMo export upstream's int8 archive is
    // quantised from, so the vocabulary and preprocessor are identical.
    private static readonly string[] GpuFiles =
    {
        "nemo128.onnx", "vocab.txt", "config.json", "decoder_joint-model.int8.onnx",
        "encoder-model.onnx", "encoder-model.onnx.data",
    };

    /// <summary>
    /// Download the GPU build of a Parakeet variant (about 2.5 GB, mostly the
    /// fp32 encoder weights). Each file lands under a .download name and is
    /// renamed when complete, so an interrupted download never looks installed;
    /// files already present are skipped, so a retry resumes file by file.
    /// </summary>
    public static async Task<string> DownloadGpuAsync(
        string modelsRoot,
        Variant variant,
        IProgress<Progress>? progress,
        CancellationToken ct)
    {
        var targetDir = Path.Combine(modelsRoot, GpuDirNameFor(variant));
        Directory.CreateDirectory(targetDir);
        var repo = variant == Variant.V3
            ? "https://huggingface.co/istupakov/parakeet-tdt-0.6b-v3-onnx/resolve/main/"
            : "https://huggingface.co/istupakov/parakeet-tdt-0.6b-v2-onnx/resolve/main/";
        for (int i = 0; i < GpuFiles.Length; i++)
        {
            var name = GpuFiles[i];
            var dest = Path.Combine(targetDir, name);
            if (File.Exists(dest)) continue;
            var label = $"Downloading GPU model, file {i + 1} of {GpuFiles.Length}";
            var fileProgress = progress is null ? null
                : new Progress<Progress>(p => progress.Report(p with { Phase = label }));
            var tmp = dest + ".download";
            await DownloadFileAsync(repo + name, tmp, fileProgress, ct).ConfigureAwait(false);
            File.Move(tmp, dest, overwrite: true);
        }
        progress?.Report(new Progress("Done", 1.0, 0, null));
        Log.Info($"GPU model {variant} ready at {targetDir}");
        return targetDir;
    }

    public static async Task<string> DownloadWhisperAsync(
        string whisperModelsDir,
        string modelName,
        IProgress<Progress>? progress,
        CancellationToken ct)
    {
        Directory.CreateDirectory(whisperModelsDir);

        var normalized = WhisperTranscriptionService.NormalizeModelName(modelName);
        var targetPath = WhisperTranscriptionService.ModelPathFor(whisperModelsDir, normalized);
        if (File.Exists(targetPath))
        {
            var length = new FileInfo(targetPath).Length;
            progress?.Report(new Progress("Done", 1.0, length, length));
            return targetPath;
        }

        var tmpPath = targetPath + ".download";
        progress?.Report(new Progress("Downloading", 0, 0, null));

        using var modelStream = await WhisperGgmlDownloader.Default
            .GetGgmlModelAsync(WhisperTranscriptionService.GgmlTypeFor(normalized))
            .ConfigureAwait(false);
        using var fileWriter = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true);

        var buffer = new byte[64 * 1024];
        long read = 0;
        int n;
        var lastReport = DateTime.UtcNow;
        while ((n = await modelStream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false)) > 0)
        {
            await fileWriter.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
            read += n;
            if ((DateTime.UtcNow - lastReport).TotalMilliseconds > 150)
            {
                progress?.Report(new Progress("Downloading", 0, read, null));
                lastReport = DateTime.UtcNow;
            }
        }

        fileWriter.Close();
        if (File.Exists(targetPath)) File.Delete(targetPath);
        File.Move(tmpPath, targetPath);

        progress?.Report(new Progress("Done", 1.0, read, read));
        Log.Info($"Whisper {normalized} model ready at {targetPath}");
        return targetPath;
    }

    private static async Task DownloadFileAsync(
        string url, string destPath, IProgress<Progress>? progress, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
                                        .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        long? total = response.Content.Headers.ContentLength;
        var buffer = new byte[64 * 1024];
        long read = 0;

        using var src = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var dst = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true);

        int n;
        var lastReport = DateTime.UtcNow;
        while ((n = await src.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false)) > 0)
        {
            await dst.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
            read += n;
            if ((DateTime.UtcNow - lastReport).TotalMilliseconds > 150)
            {
                var frac = total.HasValue && total.Value > 0 ? (double)read / total.Value : 0.0;
                progress?.Report(new Progress("Downloading", frac, read, total));
                lastReport = DateTime.UtcNow;
            }
        }
    }

    private static void ExtractTarGz(string archivePath, string destRoot)
    {
        using var fs   = File.OpenRead(archivePath);
        using var gzip = new GZipStream(fs, CompressionMode.Decompress);
        TarFile.ExtractToDirectory(gzip, destRoot, overwriteFiles: true);
    }
}
