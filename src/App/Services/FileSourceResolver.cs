namespace App26Pdf.Services;

public sealed class FileSourceResolutionException(string message, Exception? inner = null)
    : Exception(message, inner);

/// <summary>
/// Turns a classified <see cref="PdfSource"/> into a local file path <see cref="PdfEngine"/>
/// can open. Local and UNC paths are used as-is; HTTP(S) URLs are downloaded to a per-user
/// cache folder first (only plain, unauthenticated GETs are supported for now - see
/// docs/ROADMAP.md for OAuth-backed cloud storage).
/// </summary>
public sealed class FileSourceResolver
{
    private const long MaxDownloadBytes = 200 * 1024 * 1024; // 200 MB
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromSeconds(60);

    private readonly HttpClient _httpClient = new() { Timeout = DownloadTimeout };

    public async Task<string> ResolveToLocalPathAsync(PdfSource source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        return source.Kind switch
        {
            PdfSourceKind.LocalPath => ResolveLocalOrUnc(source.Value),
            PdfSourceKind.UncPath => ResolveLocalOrUnc(source.Value),
            PdfSourceKind.HttpUrl => await DownloadToCacheAsync(source.Value, cancellationToken).ConfigureAwait(false),
            _ => throw new ArgumentOutOfRangeException(nameof(source)),
        };
    }

    private static string ResolveLocalOrUnc(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileSourceResolutionException($"File non trovato: {path}");
        }

        return path;
    }

    private async Task<string> DownloadToCacheAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            if (response.Content.Headers.ContentLength is { } contentLength && contentLength > MaxDownloadBytes)
            {
                throw new FileSourceResolutionException(
                    $"Il file remoto supera il limite di download di {MaxDownloadBytes / 1024 / 1024} MB.");
            }

            var cacheDir = Path.Combine(Path.GetTempPath(), "26PDF", "cache");
            Directory.CreateDirectory(cacheDir);

            var fileName = Path.GetFileName(new Uri(url).LocalPath);
            if (string.IsNullOrWhiteSpace(fileName) || !fileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            {
                fileName = $"{Guid.NewGuid():N}.pdf";
            }

            var destinationPath = Path.Combine(cacheDir, fileName);

            await using var httpStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var fileStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write);

            await CopyWithLimitAsync(httpStream, fileStream, MaxDownloadBytes, cancellationToken).ConfigureAwait(false);
            return destinationPath;
        }
        catch (Exception ex) when (ex is not FileSourceResolutionException)
        {
            throw new FileSourceResolutionException($"Impossibile scaricare il documento da '{url}'.", ex);
        }
    }

    private static async Task CopyWithLimitAsync(Stream source, Stream destination, long maxBytes, CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > maxBytes)
            {
                throw new FileSourceResolutionException($"Il file remoto supera il limite di download di {maxBytes / 1024 / 1024} MB.");
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
    }
}
