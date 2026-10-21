using PdfEngine.Native;

namespace PdfEngine.Editing;

/// <summary>
/// What editing a document's existing text leaves alive besides the document itself: the fallback fonts
/// loaded into it (one embedding per font, however many lines use it) and the page objects that an edit
/// took off a page but an undo may still put back. Owned by <see cref="PdfDocument"/>, which releases
/// both before closing the document - PDFium objects must not outlive it.
/// </summary>
/// <remarks>Every member runs on the PDFium thread (<see cref="PdfiumRuntime"/>).</remarks>
internal sealed class PdfEditingState
{
    private readonly Dictionary<string, IntPtr> _fonts = new();
    private readonly HashSet<IntPtr> _offPageObjects = new();
    private readonly Dictionary<(IntPtr Font, char Ch), bool> _glyphSupport = new();
    private readonly Dictionary<IntPtr, string> _notdefSignature = new();

    /// <summary>The fallback font <paramref name="key"/>, loaded (and embedded) into <paramref name="document"/> on first use.</summary>
    public IntPtr GetFallbackFont(IntPtr document, string key, Func<byte[]> loadData)
    {
        if (_fonts.TryGetValue(key, out var existing))
        {
            return existing;
        }

        var data = loadData();
        var font = PdfiumNative.FPDFText_LoadFont(document, data, (uint)data.Length, PdfiumNative.FPDF_FONT_TRUETYPE, cid: true);
        if (font == IntPtr.Zero)
        {
            throw new PdfException(PdfErrorCode.Unknown, $"Impossibile caricare il font di ripiego {key}.");
        }

        _fonts[key] = font;
        return font;
    }

    /// <summary>
    /// Whether <paramref name="font"/> has a glyph for <paramref name="ch"/> - asked of PDFium itself, by drawing the
    /// character and comparing it with what a character no font has comes out as (the font's .notdef box, or nothing).
    /// PDFium has no "does this font have this glyph" call that works for every font type (a subset's /Widths still
    /// list a width for glyphs it does not contain, a CID font's codes are glyph ids), and drawing is exactly the
    /// question the user cares about.
    /// </summary>
    public bool FontHasGlyph(IntPtr document, IntPtr font, char ch)
    {
        if (char.IsWhiteSpace(ch) || char.IsControl(ch))
        {
            return true;
        }

        // Only the fallback fonts' answers are remembered: they live as long as the document. A font read off an
        // existing text object can be freed and its handle reused by another one once that object is gone.
        var cacheable = _fonts.ContainsValue(font);
        if (cacheable && _glyphSupport.TryGetValue((font, ch), out var known))
        {
            return known;
        }

        string? notdef;
        if (!cacheable || !_notdefSignature.TryGetValue(font, out notdef))
        {
            notdef = Signature(document, font, "￿") ?? string.Empty;
            if (cacheable)
            {
                _notdefSignature[font] = notdef;
            }
        }

        var signature = Signature(document, font, ch.ToString());
        var has = signature is not null && signature != notdef;
        if (cacheable)
        {
            _glyphSupport[(font, ch)] = has;
        }

        return has;
    }

    private static string? Signature(IntPtr document, IntPtr font, string text)
    {
        var obj = PdfiumNative.FPDFPageObj_CreateTextObj(document, font, 24f);
        if (obj == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            if (!PdfiumNative.FPDFText_SetText(obj, text))
            {
                return null;
            }

            var bitmap = PdfiumNative.FPDFTextObj_GetRenderedBitmap(document, IntPtr.Zero, obj, 2f);
            if (bitmap == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                var width = PdfiumNative.FPDFBitmap_GetWidth(bitmap);
                var height = PdfiumNative.FPDFBitmap_GetHeight(bitmap);
                var stride = PdfiumNative.FPDFBitmap_GetStride(bitmap);
                var pixels = new byte[stride * height];
                System.Runtime.InteropServices.Marshal.Copy(PdfiumNative.FPDFBitmap_GetBuffer(bitmap), pixels, 0, pixels.Length);
                return $"{width}x{height}:{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(pixels))}";
            }
            finally
            {
                PdfiumNative.FPDFBitmap_Destroy(bitmap);
            }
        }
        finally
        {
            PdfiumNative.FPDFPageObj_Destroy(obj);
        }
    }

    /// <summary>Starts tracking an object an edit took off a page: the state frees it (<see cref="Release"/> or document disposal) unless it is put back.</summary>
    public void Orphan(IntPtr pageObject) => _offPageObjects.Add(pageObject);

    /// <summary>An orphaned object went back on a page, which owns it again.</summary>
    public void Adopt(IntPtr pageObject) => _offPageObjects.Remove(pageObject);

    /// <summary>Frees an orphaned object the undo history no longer needs. A no-op if it was already freed or put back.</summary>
    public void Release(IntPtr pageObject)
    {
        if (_offPageObjects.Remove(pageObject))
        {
            PdfiumNative.FPDFPageObj_Destroy(pageObject);
        }
    }

    /// <summary>Frees everything still held. Must run before the document is closed.</summary>
    public void DestroyAll()
    {
        foreach (var obj in _offPageObjects)
        {
            PdfiumNative.FPDFPageObj_Destroy(obj);
        }

        _offPageObjects.Clear();
        foreach (var font in _fonts.Values)
        {
            PdfiumNative.FPDFFont_Close(font);
        }

        _fonts.Clear();
        _glyphSupport.Clear();
        _notdefSignature.Clear();
    }
}
