using System.Text;
using System.Text.RegularExpressions;

namespace PdfEngine;

/// <summary>
/// Best-effort PDF/A conformance detection from a document's own XMP metadata self-declaration -
/// the <c>pdfaid:part</c>/<c>pdfaid:conformance</c> fields every PDF/A producer is required to
/// embed (ISO 19005, Annex D), not a full PDF/A validator. A real validator (checking embedded
/// fonts, color spaces, transparency, encryption, and dozens of other structural rules against
/// the chosen conformance level) is what dedicated tools like veraPDF exist for; reporting the
/// self-declared level is what mainstream readers (Adobe Acrobat, Foxit, ...) show in their own
/// UI too - "PDF/A" on a status bar has always meant "this file claims to be", not "independently
/// verified".
/// </summary>
/// <remarks>
/// PDFium's public API has no way to read back a document's own <c>/Metadata</c> stream (checked
/// against the vendored headers - <c>FPDF_GetMetaText</c> only reads the plain Info dictionary:
/// Title/Author/Subject/.../Producer, not XMP). This works on the raw file bytes instead, which
/// every caller already has in memory from opening the document in the first place.
/// </remarks>
public static class PdfAConformance
{
    private const string NamespaceMarker = "www.aiim.org/pdfa/ns/id/";

    /// <summary>How far past the namespace marker to look for the part/conformance fields - generous for a real XMP packet, small enough to never turn into a scan of the whole file.</summary>
    private const int WindowLength = 4000;

    private static readonly Regex PartAttribute = new("pdfaid:part\\s*=\\s*\"([^\"]*)\"", RegexOptions.Compiled);
    private static readonly Regex PartElement = new("<pdfaid:part[^>]*>([^<]*)</pdfaid:part>", RegexOptions.Compiled);
    private static readonly Regex ConformanceAttribute = new("pdfaid:conformance\\s*=\\s*\"([^\"]*)\"", RegexOptions.Compiled);
    private static readonly Regex ConformanceElement = new("<pdfaid:conformance[^>]*>([^<]*)</pdfaid:conformance>", RegexOptions.Compiled);

    /// <summary>
    /// Scans <paramref name="fileBytes"/> (the raw PDF file - see remarks) for a PDF/A
    /// identification schema and returns the declared conformance (e.g. <c>"PDF/A-2B"</c>), or
    /// null if none was found - either a plain, non-PDF/A document, or (rarer) one whose
    /// <c>/Metadata</c> stream happens to be compressed (legal, just unusual - most PDF/A
    /// producers leave it uncompressed precisely so external tools can find it this way) or uses
    /// a non-standard namespace prefix instead of the conventional <c>pdfaid</c>.
    /// </summary>
    public static string? Detect(byte[] fileBytes)
    {
        // Byte-level search first, not a string.IndexOf over the whole file decoded as text: a
        // large PDF would otherwise mean allocating a full second copy of it just to look for a
        // marker that - if present at all - almost always sits near the very end (the metadata
        // stream is typically one of the last objects written). Only the small window actually
        // matched gets turned into a string.
        var markerBytes = Encoding.ASCII.GetBytes(NamespaceMarker);
        var markerIndex = fileBytes.AsSpan().IndexOf(markerBytes);
        if (markerIndex < 0)
        {
            return null;
        }

        var windowLength = Math.Min(WindowLength, fileBytes.Length - markerIndex);
        // Latin-1: every byte maps to exactly one char, so this never throws regardless of what
        // binary content follows the (always ASCII/UTF-8) XMP packet - the ASCII markers below
        // still match correctly, and nothing past the window is interpreted as anything at all.
        var window = Encoding.Latin1.GetString(fileBytes, markerIndex, windowLength);

        var part = Extract(window, PartAttribute) ?? Extract(window, PartElement);
        var conformance = Extract(window, ConformanceAttribute) ?? Extract(window, ConformanceElement);
        if (string.IsNullOrEmpty(part) || string.IsNullOrEmpty(conformance))
        {
            return null;
        }

        return $"PDF/A-{part}{conformance.ToUpperInvariant()}";
    }

    private static string? Extract(string window, Regex pattern)
    {
        var match = pattern.Match(window);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }
}
