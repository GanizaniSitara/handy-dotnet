using System;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

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
