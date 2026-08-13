namespace Pdf26.Services;

/// <summary>
/// Classifies the document argument passed to the app: local filesystem path
/// (<c>C:\docs\file.pdf</c>), UNC network path (<c>\\server\share\file.pdf</c>), or an
/// HTTP(S) URL (<c>https://example.com/file.pdf</c>).
/// </summary>
public static class CommandLineParser
{
    /// <summary>
    /// Returns the document source requested on the command line, or <c>null</c> if the app
    /// was launched with no document argument (e.g. from the Start menu).
    /// </summary>
    public static PdfSource? TryGetRequestedSource(string[] args)
    {
        // args[0] is the executable path (Environment.GetCommandLineArgs() convention);
        // the first remaining, non-flag argument is treated as the document to open.
        var arg = args.Skip(1).FirstOrDefault(a => !a.StartsWith('-') && !a.StartsWith('/'));
        return arg is null ? null : Classify(arg);
    }

    public static PdfSource Classify(string raw)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(raw);
        var value = raw.Trim().Trim('"');

        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            return new PdfSource(PdfSourceKind.HttpUrl, uri.ToString());
        }

        if (value.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return new PdfSource(PdfSourceKind.UncPath, value);
        }

        // A rooted local path, or a relative path resolved against the current directory
        // (e.g. launched from a shell in the target folder).
        var fullPath = Path.IsPathRooted(value) ? value : Path.GetFullPath(value);
        return new PdfSource(PdfSourceKind.LocalPath, fullPath);
    }
}
