namespace PdfEngine;

/// <summary>
/// A digital signature as the document declares it, read through PDFium - raw material only:
/// nothing here says whether the signature actually holds (see <see cref="PdfSignatureVerifier"/>).
/// </summary>
/// <param name="SubFilter">The signature format (<c>/SubFilter</c>), e.g. <c>adbe.pkcs7.detached</c> or <c>ETSI.CAdES.detached</c>.</param>
/// <param name="Reason">The signer's stated reason, if any.</param>
/// <param name="SigningTimeText">The <c>/M</c> entry as written (<c>D:YYYYMMDDHHMMSS+HH'mm'</c>) - only a claim by the signer; the signed CMS may carry its own time.</param>
/// <param name="ByteRange">Pairs of (offset, length) of the file bytes the signature covers - everything except the signature's own <c>/Contents</c>.</param>
/// <param name="Contents">The DER-encoded signature (a CMS/PKCS#7 container for the public-key formats), possibly followed by zero padding.</param>
public sealed record PdfSignature(
    string? SubFilter,
    string? Reason,
    string? SigningTimeText,
    IReadOnlyList<int> ByteRange,
    byte[] Contents);
