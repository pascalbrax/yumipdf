using System.Reflection;

namespace PdfEngine.Editing;

/// <summary>The look a fallback font should have: it follows the font of the line being replaced.</summary>
internal enum FallbackFamily
{
    Sans,
    Serif,
    Mono,
}

/// <summary>
/// The open-source fonts bundled for lines whose own font cannot draw what the user typed (a PDF's embedded
/// font is usually a subset: only the glyphs the document uses). Two sets, all SIL OFL 1.1, cut down to Latin,
/// Greek and Cyrillic: "PDF26" Sans/Serif/Mono (derived from Liberation, whose metrics match Arial, Times New
/// Roman and Courier New - the usual fonts of the PDFs being edited; renamed because the OFL reserves the
/// name "Liberation" for unmodified versions) and Noto Sans as the second choice.
/// </summary>
internal static class FallbackFonts
{
    /// <summary>The fonts to try for a line, best look first: the family that resembles the original, then Noto Sans.</summary>
    public static IReadOnlyList<(string Key, string DisplayName)> Chain(FallbackFamily family, bool bold, bool italic)
    {
        var style = (bold, italic) switch
        {
            (false, false) => "Regular",
            (true, false) => "Bold",
            (false, true) => "Italic",
            _ => "BoldItalic",
        };

        var primary = family switch
        {
            FallbackFamily.Serif => "PDF26Serif",
            FallbackFamily.Mono => "PDF26Mono",
            _ => "PDF26Sans",
        };

        return new[]
        {
            ($"{primary}-{style}", $"{primary}-{style}"),
            ($"NotoSans-{style}", $"NotoSans-{style}"),
        };
    }

    /// <summary>Reads a bundled font file (the key is the file name without ".ttf").</summary>
    public static byte[] Load(string key)
    {
        var name = $"PdfEngine.Fonts.{key}.ttf";
        using var stream = typeof(FallbackFonts).Assembly.GetManifestResourceStream(name)
            ?? throw new PdfException(PdfErrorCode.Unknown, $"Font incorporato mancante: {key}.");
        using var memory = new MemoryStream((int)stream.Length);
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    /// <summary>
    /// Chooses the family, weight and slant of the fallback font. In order of trust: what the font file itself says about
    /// its shapes (<paramref name="look"/>, from <see cref="SfntInfo"/>), the font's name, and last the descriptor flags -
    /// which producers fill in carelessly (a bold sans font seen declared serif). Bold and italic are on if any source says so.
    /// </summary>
    public static (FallbackFamily Family, bool Bold, bool Italic) Classify(string baseFontName, int flags, int weight, int italicAngle, FontLook look = default)
    {
        var name = baseFontName.ToLowerInvariant();

        var family = look.Family
            ?? (ContainsAny(name, "courier", "mono", "consol", "typewriter", "lucidaconsole") ? (FallbackFamily?)FallbackFamily.Mono : null)
            ?? (!name.Contains("sans") && ContainsAny(name, "times", "serif", "georgia", "garamond", "cambria", "minion", "palatino", "bookman", "century", "baskerville", "didot", "caslon") ? (FallbackFamily?)FallbackFamily.Serif : null)
            ?? (ContainsAny(name, "arial", "helvetica", "sans", "verdana", "tahoma", "calibri", "segoe", "roboto", "open") ? (FallbackFamily?)FallbackFamily.Sans : null)
            ?? (flags > 0 && (flags & 1) != 0 ? (FallbackFamily?)FallbackFamily.Mono : null)
            ?? FallbackFamily.Sans;

        var bold = look.Bold == true || weight is >= 600 and <= 1000 || (flags > 0 && (flags & 0x40000) != 0) || ContainsAny(name, "bold", "black", "heavy", "demi");
        var italic = look.Italic == true || italicAngle != 0 || (flags > 0 && (flags & 0x40) != 0) || ContainsAny(name, "italic", "oblique");
        return (family, bold, italic);
    }

    private static bool ContainsAny(string text, params string[] needles) => needles.Any(text.Contains);
}
