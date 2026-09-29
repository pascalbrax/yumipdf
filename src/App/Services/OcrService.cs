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
    /// Recognizes text in a BGRA8 page render (the same buffer shape <c>PdfPage.RenderToBgra</c>
    /// produces). Returns the whole page's text as <see cref="OcrResult.Text"/> already joins it -
    /// good enough for keyword search, which is all <c>MainViewModel</c>'s OCR pass is for today;
    /// word-level positions were investigated (docs/ARCHITECTURE.md "OCR") but a real bug in
    /// placing them was never root-caused, so this deliberately does not attempt per-word placement.
    /// </summary>
    public async Task<string> RecognizeAsync(byte[] bgra, int pixelWidth, int pixelHeight)
    {
        using var bitmap = new SoftwareBitmap(BitmapPixelFormat.Bgra8, pixelWidth, pixelHeight, BitmapAlphaMode.Ignore);
        bitmap.CopyFromBuffer(bgra.AsBuffer());

        var result = await _engine!.RecognizeAsync(bitmap);
        return result.Text;
    }
}
