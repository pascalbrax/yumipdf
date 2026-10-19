using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using PdfEngine;
using Xunit;

namespace PdfEngine.Tests;

public class PdfSignatureTests
{
    private const int ContentsHexLength = 8000;

    private const string CatalogBody = "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [5 0 R] /SigFlags 3 >> >>";

    [Fact]
    public void GetSignatures_ListsTheDeclaredSignature()
    {
        var file = BuildSignedPdf();

        using var document = PdfDocument.OpenFromBytes(file);
        var signatures = document.GetSignatures();

        var signature = Assert.Single(signatures);
        Assert.Equal("adbe.pkcs7.detached", signature.SubFilter);
        Assert.Equal("D:20261007075807+00'00'", signature.SigningTimeText);
        Assert.Equal(4, signature.ByteRange.Count);
        Assert.NotEmpty(signature.Contents);
    }

    [Fact]
    public void Verify_UntouchedFile_IsIntactAndNamesTheSigner()
    {
        var file = BuildSignedPdf();

        var report = Verify(file);

        Assert.Equal(PdfSignatureStatus.Intact, report.Overall);
        var check = Assert.Single(report.Signatures);
        Assert.Equal("Test Signer", check.SignerName);
        Assert.True(check.ReachesEndOfFile);
        Assert.NotNull(check.SigningTime);
    }

    [Fact]
    public void Verify_ByteChangedInsideTheSignedPart_IsAltered()
    {
        var file = BuildSignedPdf();
        var marker = Encoding.ASCII.GetBytes("%original-comment");
        var at = file.AsSpan().IndexOf(marker);
        Assert.True(at >= 0);
        file[at + 1] = (byte)'X';

        var report = Verify(file);

        Assert.Equal(PdfSignatureStatus.Altered, report.Overall);
    }

    [Fact]
    public void Verify_PageAddedAfterSigning_IsChangedAfterSigning()
    {
        var file = Append(BuildSignedPdf(), "\n7 0 obj\n<< /Type /Page /Parent 2 0 R >>\nendobj\n");

        var report = Verify(file);

        Assert.Equal(PdfSignatureStatus.ChangedAfterSigning, report.Overall);
        Assert.False(report.Signatures[0].ReachesEndOfFile);
    }

    [Fact]
    public void Verify_OnlyValidationDataAddedAfterSigning_StaysIntact()
    {
        // What a signing service appends for long-term validation: the same catalog plus a /DSS,
        // and the certificate/OCSP/CRL streams it points to.
        var file = Append(
            BuildSignedPdf(),
            "\n1 0 obj\n" + CatalogBody.Replace(" >> >>", " >> /DSS 7 0 R >>") + "\nendobj\n" +
            "7 0 obj\n<< /VRI 8 0 R /OCSPs [9 0 R] >>\nendobj\n" +
            "8 0 obj\n<< /ABCDEF 9 0 R >>\nendobj\n" +
            "9 0 obj\n<< /Length 3 >>\nstream\nabc\nendstream\nendobj\n");

        var report = Verify(file);

        Assert.Equal(PdfSignatureStatus.Intact, report.Overall);
    }

    [Fact]
    public void Verify_DssUpdateThatAlsoChangesTheCatalog_IsChangedAfterSigning()
    {
        var file = Append(
            BuildSignedPdf(),
            "\n1 0 obj\n" + CatalogBody.Replace(" >> >>", " >> /DSS 7 0 R /OpenAction 8 0 R >>") + "\nendobj\n" +
            "7 0 obj\n<< /Certs [9 0 R] >>\nendobj\n" +
            "9 0 obj\n<< /Length 3 >>\nstream\nabc\nendstream\nendobj\n");

        var report = Verify(file);

        Assert.Equal(PdfSignatureStatus.ChangedAfterSigning, report.Overall);
    }

    [Fact]
    public void Verify_UnsupportedSignatureFormat_IsUnverifiable()
    {
        var file = BuildSignedPdf(subFilter: "adbe.x509.rsa_sha1");

        var report = Verify(file);

        Assert.Equal(PdfSignatureStatus.Unverifiable, report.Overall);
    }

    [Fact]
    public void Verify_DocumentWithoutSignatures_HasNothingToReport()
    {
        using var document = PdfDocument.OpenFromPath(Path.Combine(AppContext.BaseDirectory, "Fixtures", "text-page.pdf"));

        Assert.Empty(document.GetSignatures());
    }

    [Theory]
    [InlineData("D:20261007075807+00'00'", 2026, 10, 7, 7, 58, 7, 0)]
    [InlineData("D:20261007095807+02'00'", 2026, 10, 7, 9, 58, 7, 120)]
    [InlineData("D:20261007075807-05'30'", 2026, 10, 7, 7, 58, 7, -330)]
    [InlineData("D:20261007075807Z", 2026, 10, 7, 7, 58, 7, 0)]
    [InlineData("D:2026", 2026, 1, 1, 0, 0, 0, 0)]
    public void ParsePdfDate_ReadsPdfDates(string text, int y, int mo, int d, int h, int mi, int s, int offsetMinutes)
    {
        var parsed = PdfSignatureVerifier.ParsePdfDate(text);

        Assert.Equal(new DateTimeOffset(y, mo, d, h, mi, s, TimeSpan.FromMinutes(offsetMinutes)), parsed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("yesterday")]
    [InlineData("D:2026139900")]
    public void ParsePdfDate_NotADate_ReturnsNull(string? text)
    {
        Assert.Null(PdfSignatureVerifier.ParsePdfDate(text));
    }

    // ---- helpers -------------------------------------------------------------------------

    private static PdfSignatureReport Verify(byte[] file)
    {
        using var document = PdfDocument.OpenFromBytes(file);
        return PdfSignatureVerifier.Verify(file, document.GetSignatures());
    }

    private static byte[] Append(byte[] file, string text)
    {
        var extra = Encoding.Latin1.GetBytes(text);
        var result = new byte[file.Length + extra.Length];
        file.CopyTo(result, 0);
        extra.CopyTo(result, file.Length);
        return result;
    }

    /// <summary>
    /// A one-page PDF carrying a real detached CMS signature (made with a throwaway self-signed
    /// certificate) over everything except its own /Contents - built by hand, like the other
    /// fixtures, but at test time because the signature has to match the bytes it covers.
    /// </summary>
    private static byte[] BuildSignedPdf(string subFilter = "adbe.pkcs7.detached")
    {
        const string placeholder = "0000000000";
        string Assemble(string a, string b, string c, string hex)
        {
            var objects = new[]
            {
                "1 0 obj\n" + CatalogBody + "\nendobj\n",
                "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n",
                "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << >> /Annots [5 0 R] >>\nendobj\n",
                "5 0 obj\n<< /Type /Annot /Subtype /Widget /FT /Sig /T (Sig1) /Rect [0 0 0 0] /V 6 0 R /P 3 0 R >>\nendobj\n",
                "6 0 obj\n<< /Type /Sig /Filter /Adobe.PPKLite /SubFilter /" + subFilter + " /M (D:20261007075807+00'00') " +
                    "/ByteRange [" + a + " " + b + " " + c + " " + placeholder + "] /Contents <" + hex + "> >>\nendobj\n",
            };

            var text = new StringBuilder("%PDF-1.7\n%original-comment\n");
            var offsets = new List<int>();
            foreach (var o in objects)
            {
                offsets.Add(text.Length);
                text.Append(o);
            }

            var xref = text.Length;
            text.Append("xref\n0 7\n0000000000 65535 f \n");
            var byNumber = new Dictionary<int, int> { [1] = offsets[0], [2] = offsets[1], [3] = offsets[2], [5] = offsets[3], [6] = offsets[4] };
            for (var n = 1; n <= 6; n++)
            {
                text.Append(byNumber.TryGetValue(n, out var off) ? $"{off:D10} 00000 n \n" : "0000000000 65535 f \n");
            }

            text.Append("trailer\n<< /Size 7 /Root 1 0 R >>\nstartxref\n" + xref + "\n%%EOF\n");
            return text.ToString();
        }

        // Pass 1 with zeros finds where /Contents sits (all numbers are fixed-width, so pass 2 has the same layout).
        var zeros = new string('0', ContentsHexLength);
        var draft = Assemble(placeholder, placeholder, placeholder, zeros);
        var holeStart = draft.IndexOf("/Contents <", StringComparison.Ordinal) + "/Contents ".Length;
        var holeEnd = holeStart + ContentsHexLength + 2;
        // The last number is the length of the tail - written right back into the same fixed-width slot.
        var tailLength = draft.Length - holeEnd;
        var layout = Assemble(placeholder, holeStart.ToString("D10"), holeEnd.ToString("D10"), zeros)
            .Replace("/ByteRange [" + placeholder + " " + holeStart.ToString("D10") + " " + holeEnd.ToString("D10") + " " + placeholder + "]",
                     "/ByteRange [" + placeholder + " " + holeStart.ToString("D10") + " " + holeEnd.ToString("D10") + " " + tailLength.ToString("D10") + "]");
        var bytes = Encoding.Latin1.GetBytes(layout);

        var signed = new byte[holeStart + (bytes.Length - holeEnd)];
        Buffer.BlockCopy(bytes, 0, signed, 0, holeStart);
        Buffer.BlockCopy(bytes, holeEnd, signed, holeStart, bytes.Length - holeEnd);

        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=Test Signer", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        var cms = new SignedCms(new ContentInfo(signed), detached: true);
        cms.ComputeSignature(new CmsSigner(SubjectIdentifierType.IssuerAndSerialNumber, certificate) { DigestAlgorithm = new Oid("2.16.840.1.101.3.4.2.1") }, silent: true);
        var hex = Convert.ToHexString(cms.Encode());
        Assert.True(hex.Length <= ContentsHexLength, "test signature larger than its placeholder");

        var withSignature = layout.Remove(holeStart + 1, ContentsHexLength).Insert(holeStart + 1, hex.PadRight(ContentsHexLength, '0'));
        return Encoding.Latin1.GetBytes(withSignature);
    }
}
