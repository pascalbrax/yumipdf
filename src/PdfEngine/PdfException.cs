using PdfEngine.Native;

namespace PdfEngine;

public sealed class PdfException : Exception
{
    public PdfErrorCode ErrorCode { get; }

    public PdfException(PdfErrorCode errorCode, string message) : base(message)
    {
        ErrorCode = errorCode;
    }

    internal static PdfException FromLastPdfiumError(string context)
    {
        var code = (PdfErrorCode)PdfiumNative.FPDF_GetLastError();
        return new PdfException(code, $"{context} (PDFium error: {code})");
    }
}

public enum PdfErrorCode
{
    Success = 0,
    Unknown = 1,
    File = 2,
    Format = 3,
    Password = 4,
    Security = 5,
    Page = 6,
}
