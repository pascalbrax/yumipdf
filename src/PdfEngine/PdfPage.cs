using PdfEngine.Native;

namespace PdfEngine;

/// <summary>A single loaded PDF page. Owned by the <see cref="PdfDocument"/> that created it.</summary>
public sealed class PdfPage : IDisposable
{
    private readonly IntPtr _handle;
    private bool _disposed;

    internal PdfPage(IntPtr handle, int index)
    {
        _handle = handle;
        Index = index;
    }

    public int Index { get; }

    /// <summary>Page size in PDF points (1/72 inch), independent of rotation.</summary>
    public (double Width, double Height) SizeInPoints
    {
        get
        {
            ThrowIfDisposed();
            return (PdfiumNative.FPDF_GetPageWidth(_handle), PdfiumNative.FPDF_GetPageHeight(_handle));
        }
    }

    /// <summary>
    /// Renders the page to a top-down 32bpp BGRA pixel buffer at the given pixel size.
    /// Callers typically size <paramref name="pixelWidth"/>/<paramref name="pixelHeight"/>
    /// from <see cref="SizeInPoints"/> scaled by a zoom/DPI factor.
    /// </summary>
    public byte[] RenderToBgra(int pixelWidth, int pixelHeight, bool renderFormFields = true)
    {
        ThrowIfDisposed();
        if (pixelWidth <= 0 || pixelHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pixelWidth), "Le dimensioni di rendering devono essere positive.");
        }

        var stride = pixelWidth * 4;
        var buffer = new byte[stride * pixelHeight];
        var bufferHandle = System.Runtime.InteropServices.GCHandle.Alloc(buffer, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            var bitmap = PdfiumNative.FPDFBitmap_CreateEx(
                pixelWidth, pixelHeight, PdfiumNative.FPDFBitmap_BGRA, bufferHandle.AddrOfPinnedObject(), stride);
            if (bitmap == IntPtr.Zero)
            {
                throw PdfException.FromLastPdfiumError("Impossibile creare il bitmap di rendering.");
            }

            try
            {
                PdfiumNative.FPDFBitmap_FillRect(bitmap, 0, 0, pixelWidth, pixelHeight, 0xFFFFFFFF);
                var flags = renderFormFields ? PdfiumNative.FPDF_ANNOT | PdfiumNative.FPDF_LCD_TEXT : PdfiumNative.FPDF_LCD_TEXT;
                PdfiumNative.FPDF_RenderPageBitmap(bitmap, _handle, 0, 0, pixelWidth, pixelHeight, /* rotate */ 0, flags);
            }
            finally
            {
                PdfiumNative.FPDFBitmap_Destroy(bitmap);
            }
        }
        finally
        {
            bufferHandle.Free();
        }

        return buffer;
    }

    internal IntPtr Handle
    {
        get
        {
            ThrowIfDisposed();
            return _handle;
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(PdfPage));
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        PdfiumNative.FPDF_ClosePage(_handle);
        _disposed = true;
    }
}
