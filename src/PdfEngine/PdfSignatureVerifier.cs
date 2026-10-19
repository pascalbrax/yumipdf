using System.Formats.Asn1;
using System.Globalization;
using System.Text;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;

namespace PdfEngine;

/// <summary>How one signature (or, for <see cref="PdfSignatureReport.Overall"/>, the whole document) stands.</summary>
public enum PdfSignatureStatus
{
    /// <summary>The signed content is unchanged, and the signature covers the file to its very end.</summary>
    Intact,

    /// <summary>The signed content is unchanged, but the file continues past what the signature covers - later edits, or later signatures.</summary>
    ChangedAfterSigning,

    /// <summary>The signed content does not match the signature (modified, or damaged).</summary>
    Altered,

    /// <summary>A signature is there but cannot be checked here (unsupported format, unreadable, no certificate inside).</summary>
    Unverifiable,
}

/// <summary>The outcome for one signature.</summary>
/// <param name="Status">See <see cref="PdfSignatureStatus"/>; this signature alone.</param>
/// <param name="SignerName">Common name of the signer's certificate, when the signature carries it.</param>
/// <param name="SigningTime">Time of signing: the signed CMS's own claim if it has one, otherwise the PDF's <c>/M</c>.</param>
/// <param name="ReachesEndOfFile">Whether the covered bytes run to the end of the file.</param>
/// <param name="Detail">Why, for the two non-happy outcomes - already worded for the user.</param>
public sealed record PdfSignatureCheck(
    PdfSignature Signature,
    PdfSignatureStatus Status,
    string? SignerName,
    DateTimeOffset? SigningTime,
    bool ReachesEndOfFile,
    string? Detail);

/// <summary>The outcome for a whole document.</summary>
/// <param name="Overall">Worst outcome among the signatures; <see cref="PdfSignatureStatus.Intact"/> only if all hold and the latest one reaches the end of the file.</param>
public sealed record PdfSignatureReport(PdfSignatureStatus Overall, IReadOnlyList<PdfSignatureCheck> Signatures)
{
    /// <summary>The signature that covers the most of the file - the one a reader means by "the signature".</summary>
    public PdfSignatureCheck? Latest => Signatures.MaxBy(c => PdfSignatureVerifier.CoveredEnd(c.Signature));
}

/// <summary>
/// Integrity check of a document's digital signatures: whether what was signed is still what is
/// in the file. Deliberately NOT a trust check - the signer's certificate is not validated
/// against any trusted root, nor checked for expiry or revocation. That is what Acrobat's "valid
/// signature" does on top of this, and it needs a trust store and network revocation lookups;
/// every message built on this result has to say "integrity", never "valid signer".
/// </summary>
/// <remarks>
/// PDFium only lists signatures (<see cref="PdfDocument.GetSignatures"/>); it never verifies
/// them. The check works on the raw file bytes, like <see cref="PdfAConformance"/>: the signed
/// bytes are the file's <c>/ByteRange</c> pieces, the signature is a detached CMS container, and
/// .NET's <see cref="SignedCms"/> does the cryptography (signature over the signed attributes,
/// which carry the digest of the content).
/// </remarks>
public static class PdfSignatureVerifier
{
    private static readonly string[] SupportedSubFilters = { "adbe.pkcs7.detached", "ETSI.CAdES.detached" };
    private const string SigningTimeOid = "1.2.840.113549.1.9.5";

    public static PdfSignatureReport Verify(byte[] fileBytes, IReadOnlyList<PdfSignature> signatures)
    {
        var checks = signatures.Select(signature => Check(fileBytes, signature)).ToList();
        return new PdfSignatureReport(Summarize(checks), checks);
    }

    private static PdfSignatureStatus Summarize(List<PdfSignatureCheck> checks)
    {
        if (checks.Count == 0)
        {
            return PdfSignatureStatus.Intact;
        }

        if (checks.Any(c => c.Status == PdfSignatureStatus.Altered))
        {
            return PdfSignatureStatus.Altered;
        }

        if (checks.Any(c => c.Status == PdfSignatureStatus.Unverifiable))
        {
            return PdfSignatureStatus.Unverifiable;
        }

        // Every signature but the last one is normally followed by more file (the next signature
        // is itself an addition) - so only the signature that ends furthest decides whether
        // anything came after the signing as a whole.
        return checks.MaxBy(c => CoveredEnd(c.Signature))!.Status;
    }

    internal static long CoveredEnd(PdfSignature signature)
        => signature.ByteRange.Count >= 2 ? (long)signature.ByteRange[^2] + signature.ByteRange[^1] : 0;

    private static PdfSignatureCheck Check(byte[] file, PdfSignature signature)
    {
        PdfSignatureCheck Result(PdfSignatureStatus status, string? detail, bool reachesEnd = false, string? signer = null, DateTimeOffset? time = null)
            => new(signature, status, signer, time ?? ParsePdfDate(signature.SigningTimeText), reachesEnd, detail);

        if (signature.SubFilter is null || !SupportedSubFilters.Contains(signature.SubFilter, StringComparer.Ordinal))
        {
            return Result(PdfSignatureStatus.Unverifiable, $"formato di firma non supportato ({signature.SubFilter ?? "sconosciuto"})");
        }

        if (!TryGetSignedBytes(file, signature.ByteRange, out var signed, out var reachesEnd))
        {
            return Result(PdfSignatureStatus.Unverifiable, "intervallo di byte della firma non valido");
        }

        var contents = TrimToEncodedValue(signature.Contents);
        SignedCms cms;
        try
        {
            cms = new SignedCms(new ContentInfo(signed), detached: true);
            cms.Decode(contents);
        }
        catch (CryptographicException)
        {
            return Result(PdfSignatureStatus.Unverifiable, "contenitore della firma illeggibile", reachesEnd);
        }

        if (cms.SignerInfos.Count == 0)
        {
            return Result(PdfSignatureStatus.Unverifiable, "nessun firmatario nella firma", reachesEnd);
        }

        var signerInfo = cms.SignerInfos[0];
        var certificate = signerInfo.Certificate;
        var signer = certificate?.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
        var time = ReadSigningTime(signerInfo);
        if (certificate is null)
        {
            return Result(PdfSignatureStatus.Unverifiable, "certificato del firmatario non incluso nella firma", reachesEnd, signer, time);
        }

        try
        {
            // verifySignatureOnly: checks the content digest and the signature over it, and
            // nothing about whether the certificate is trusted - see the class remarks.
            cms.CheckSignature(verifySignatureOnly: true);
        }
        catch (CryptographicException)
        {
            return Result(
                PdfSignatureStatus.Altered,
                "il contenuto non corrisponde a quanto è stato firmato (modificato o danneggiato)",
                reachesEnd, signer, time);
        }

        if (reachesEnd)
        {
            return Result(PdfSignatureStatus.Intact, null, reachesEnd, signer, time);
        }

        return IsValidationDataOnly(file, CoveredEnd(signature))
            ? Result(PdfSignatureStatus.Intact, "seguita solo da dati di validazione (DSS), che non cambiano il documento", reachesEnd, signer, time)
            : Result(PdfSignatureStatus.ChangedAfterSigning, "il file prosegue con modifiche o altre firme dopo questa", reachesEnd, signer, time);
    }

    // ---- What follows the signed part ---------------------------------------------------

    private static readonly Regex IndirectObject = new(@"(?<num>\d+)\s+\d+\s+obj\b(?<body>.*?)endobj", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex ObjectHeader = new(@"\b\d+\s+\d+\s+obj\b", RegexOptions.Compiled);
    private static readonly Regex Reference = new(@"(\d+)\s+\d+\s+R\b", RegexOptions.Compiled);
    private static readonly Regex DssEntry = new(@"/DSS\s+(\d+)\s+\d+\s+R", RegexOptions.Compiled);
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    /// <summary>
    /// Whether everything the file adds after <paramref name="tailStart"/> is only PAdES
    /// validation data - the Document Security Store (<c>/DSS</c>: certificates, OCSP answers,
    /// CRLs) that signing services append so the signature can be checked long after the
    /// certificates expired. That is normal for a signed document and changes nothing a reader
    /// sees; calling it "modified after signing" would be a false alarm on every such file.
    /// </summary>
    /// <remarks>
    /// Deliberately conservative: it answers yes only when every object added is the catalog
    /// (identical to its previous version apart from the <c>/DSS</c> entry), something reachable
    /// from the <c>/DSS</c> dictionary, or a cross-reference stream. Anything else - a page, an
    /// annotation, a form field, an object it cannot classify, an object hiding inside another
    /// one's data, a catalog it cannot compare - answers no, and the document is reported as
    /// changed after signing.
    /// </remarks>
    private static bool IsValidationDataOnly(byte[] file, long tailStart)
    {
        if (tailStart <= 0 || tailStart >= file.LongLength)
        {
            return false;
        }

        var tail = Encoding.Latin1.GetString(file, (int)tailStart, file.Length - (int)tailStart);
        var objects = new Dictionary<int, string>();
        foreach (Match match in IndirectObject.Matches(tail))
        {
            var body = match.Groups["body"].Value;
            if (ObjectHeader.IsMatch(body) || !int.TryParse(match.Groups["num"].Value, out var number))
            {
                return false;
            }

            objects[number] = body;
        }

        var catalog = objects.FirstOrDefault(o => HasName(o.Value, "/Type", "/Catalog"));
        if (catalog.Value is null || DssEntry.Match(catalog.Value) is not { Success: true } dss)
        {
            return false;
        }

        var previousCatalog = LastDefinition(file, tailStart, catalog.Key);
        if (previousCatalog is null || NormalizeCatalog(previousCatalog) != NormalizeCatalog(catalog.Value))
        {
            return false;
        }

        // Everything reachable from /DSS is validation data; the rest must be bookkeeping.
        var allowed = new HashSet<int>();
        var pending = new Stack<int>();
        pending.Push(int.Parse(dss.Groups[1].Value));
        while (pending.Count > 0)
        {
            var number = pending.Pop();
            if (!allowed.Add(number) || !objects.TryGetValue(number, out var body))
            {
                continue;
            }

            foreach (Match reference in Reference.Matches(DictionaryPart(body)))
            {
                pending.Push(int.Parse(reference.Groups[1].Value));
            }
        }

        return objects.All(o => o.Key == catalog.Key || allowed.Contains(o.Key) || HasName(o.Value, "/Type", "/XRef"));
    }

    /// <summary>The object's dictionary - everything before its stream data, which can be binary and says nothing about structure.</summary>
    private static string DictionaryPart(string body)
    {
        var stream = body.IndexOf("stream", StringComparison.Ordinal);
        return stream < 0 ? body : body[..stream];
    }

    private static bool HasName(string body, string key, string name)
        => Regex.IsMatch(DictionaryPart(body), Regex.Escape(key) + @"\s*" + Regex.Escape(name) + @"(?![A-Za-z0-9])");

    /// <summary>The body of the last definition of object <paramref name="number"/> before <paramref name="limit"/> - the version the update replaces.</summary>
    private static string? LastDefinition(byte[] file, long limit, int number)
    {
        var head = Encoding.Latin1.GetString(file, 0, (int)limit);
        var pattern = new Regex(@"(?<![0-9])" + number + @"\s+\d+\s+obj\b(?<body>.*?)endobj", RegexOptions.Singleline);
        string? last = null;
        foreach (Match match in pattern.Matches(head))
        {
            last = match.Groups["body"].Value;
        }

        return last;
    }

    private static string NormalizeCatalog(string body)
        => Whitespace.Replace(DssEntry.Replace(body, string.Empty), " ").Trim();

    /// <summary>
    /// Concatenates the file pieces the signature covers. Refuses anything malformed (pieces out
    /// of the file, overlapping, out of order, or not leaving exactly one gap shaped like a hex
    /// string for the signature itself) - a signature whose range does not describe "the file
    /// minus the signature value" proves nothing about the file.
    /// </summary>
    private static bool TryGetSignedBytes(byte[] file, IReadOnlyList<int> range, out byte[] signed, out bool reachesEnd)
    {
        signed = Array.Empty<byte>();
        reachesEnd = false;
        if (range.Count < 4 || range.Count % 2 != 0)
        {
            return false;
        }

        long previousEnd = 0;
        long total = 0;
        for (var i = 0; i < range.Count; i += 2)
        {
            long start = range[i];
            long length = range[i + 1];
            if (start < previousEnd || length < 0 || start + length > file.LongLength)
            {
                return false;
            }

            previousEnd = start + length;
            total += length;
        }

        // The gap between the first two pieces is the signature's own <hex> string.
        long gapStart = (long)range[0] + range[1];
        long gapEnd = range[2];
        if (gapEnd - gapStart < 2 || file[gapStart] != (byte)'<' || file[gapEnd - 1] != (byte)'>')
        {
            return false;
        }

        var result = new byte[total];
        var offset = 0;
        for (var i = 0; i < range.Count; i += 2)
        {
            Buffer.BlockCopy(file, range[i], result, offset, range[i + 1]);
            offset += range[i + 1];
        }

        signed = result;
        reachesEnd = previousEnd == file.LongLength;
        return true;
    }

    /// <summary>The <c>/Contents</c> hex string is padded with zeros to a fixed size; the CMS inside is a single encoded value and anything after it is padding.</summary>
    private static byte[] TrimToEncodedValue(byte[] contents)
    {
        try
        {
            AsnDecoder.ReadEncodedValue(contents, AsnEncodingRules.BER, out _, out _, out var consumed);
            return consumed < contents.Length ? contents[..consumed] : contents;
        }
        catch (AsnContentException)
        {
            return contents;
        }
    }

    private static DateTimeOffset? ReadSigningTime(SignerInfo signerInfo)
    {
        foreach (var attribute in signerInfo.SignedAttributes)
        {
            if (attribute.Oid?.Value == SigningTimeOid && attribute.Values.Count > 0 && attribute.Values[0] is Pkcs9SigningTime time)
            {
                return new DateTimeOffset(time.SigningTime.ToUniversalTime(), TimeSpan.Zero);
            }
        }

        return null;
    }

    private static readonly Regex PdfDate = new(
        @"^D:(\d{4})(\d{2})?(\d{2})?(\d{2})?(\d{2})?(\d{2})?(?:(Z)|([+-])(\d{2})'?(\d{2})?'?)?",
        RegexOptions.Compiled);

    /// <summary>Parses a PDF date string (<c>D:YYYYMMDDHHMMSS+HH'mm'</c>, everything after the year optional); null if it does not look like one.</summary>
    public static DateTimeOffset? ParsePdfDate(string? text)
    {
        if (text is null || PdfDate.Match(text) is not { Success: true } m)
        {
            return null;
        }

        int Part(int group, int fallback) => m.Groups[group].Success ? int.Parse(m.Groups[group].Value, CultureInfo.InvariantCulture) : fallback;

        var offset = TimeSpan.Zero;
        if (m.Groups[8].Success)
        {
            var sign = m.Groups[8].Value == "-" ? -1 : 1;
            offset = new TimeSpan(sign * Part(9, 0), sign * Part(10, 0), 0);
        }

        try
        {
            return new DateTimeOffset(Part(1, 1), Part(2, 1), Part(3, 1), Part(4, 0), Part(5, 0), Part(6, 0), offset);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }
}
