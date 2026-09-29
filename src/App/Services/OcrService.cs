using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace Pdf26.Services;

/// <summary>
/// Recognizes text in a rendered page bitmap via the Windows-provided <c>Windows.Media.Ocr</c>
/// engine - no extra package: it needs nothing beyond the Windows SDK projection this project's
/// TFM (<c>net10.0-windows10.0.19041.0</c>) already carries, confirmed live before writing this
/// (a probe referencing only <c>PdfEngine</c>, no Windows App SDK, called it successfully).
/// </summary>
/// <remarks>
/// Depends on a per-language OCR component being installed in Windows (Impostazioni &gt; Ora e
/// lingua &gt; Lingua e area geografica): unlike the OCR alternatives considered for this project
/// (RapidOCR/Tesseract, tracked in docs/ARCHITECTURE.md "OCR"), this one ships no model of its
/// own, so its availability and quality depend on what the user's copy of Windows has installed -
/// confirmed live that a language with no component returns a <see langword="null"/> engine, not
/// a slow/degraded one. <see cref="TryCreate"/> is the single place that decision is made.
/// </remarks>
public sealed class OcrService
{
    private readonly OcrEngine? _engine;

    private OcrService(OcrEngine engine) => _engine = engine;

    /// <summary><see langword="null"/> if no OCR-capable language is installed at all - see the type's remarks.</summary>
    public static OcrService? TryCreate()
    {
        // Prefer the app's own UI language (it-IT for this app) over whatever the user's overall
        // Windows profile languages resolve to - confirmed live that the two can disagree (a
        // profile mixing it-IT and de-DE tried de-DE first via TryCreateFromUserProfileLanguages).
        var preferred = OcrEngine.TryCreateFromLanguage(new Language("it"));
        var engine = preferred ?? OcrEngine.TryCreateFromUserProfileLanguages();
        return engine is null ? null : new OcrService(engine);
    }

    public string RecognizerLanguageTag => _engine!.RecognizerLanguage.LanguageTag;

    /// <summary>
    /// One recognized word and its bounding box in the pixel space of the bitmap that was
    /// recognized - for the on-page search-highlight overlay (<c>MainViewModel</c>). Never
    /// written into the PDF itself: the "sandwich" (an invisible text layer positioned per
    /// word, so the recognized text becomes part of the file) was investigated separately and
    /// had a real, unresolved rendering bug for a minority of words - see docs/ARCHITECTURE.md
    /// "OCR". A screen-only overlay never goes through that code path.
    /// </summary>
    public sealed record OcrWord(string Text, double X, double Y, double Width, double Height);

    public sealed record OcrPageResult(string Text, IReadOnlyList<OcrWord> Words);

    /// <summary>
    /// Recognizes text in a BGRA8 page render (the same buffer shape <c>PdfPage.RenderToBgra</c>
    /// produces). <see cref="OcrPageResult.Text"/> is <see cref="OcrResult.Text"/>, already
    /// joined by Windows - what keyword search matches against; <see cref="OcrPageResult.Words"/>
    /// is only for the highlight overlay (see <see cref="OcrWord"/>'s remarks on why it never
    /// feeds back into the PDF itself).
    /// </summary>
    public async Task<OcrPageResult> RecognizeAsync(byte[] bgra, int pixelWidth, int pixelHeight)
    {
        using var bitmap = new SoftwareBitmap(BitmapPixelFormat.Bgra8, pixelWidth, pixelHeight, BitmapAlphaMode.Ignore);
        bitmap.CopyFromBuffer(bgra.AsBuffer());

        var result = await _engine!.RecognizeAsync(bitmap);
        var words = result.Lines
            .SelectMany(line => line.Words)
            .Select(word => new OcrWord(word.Text, word.BoundingRect.X, word.BoundingRect.Y, word.BoundingRect.Width, word.BoundingRect.Height))
            .ToList();
        return new OcrPageResult(result.Text, words);
    }
}
