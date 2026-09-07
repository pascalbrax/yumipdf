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
        => PdfiumRuntime.Invoke(() =>
        {
            ThrowIfDisposed();
            return (PdfiumNative.FPDF_GetPageWidth(_handle), PdfiumNative.FPDF_GetPageHeight(_handle));
        });

    /// <summary>
    /// Renders the page to a top-down 32bpp BGRA pixel buffer at the given pixel size.
    /// Callers typically size <paramref name="pixelWidth"/>/<paramref name="pixelHeight"/>
    /// from <see cref="SizeInPoints"/> scaled by a zoom/DPI factor.
    /// </summary>
    /// <remarks>
    /// Shows each form field's last-<em>generated</em> appearance stream - accurate for a
    /// document as originally authored, but a value just changed via
    /// <see cref="Forms.PdfFormFiller.SetFieldValue"/>/<c>SetChecked</c> and not yet flattened
    /// will not show up here. Use <see cref="Forms.PdfFormFiller.RenderPageToBgra"/> instead
    /// once a document has an open <see cref="Forms.PdfFormFiller"/> - see its remarks.
    /// </remarks>
    public byte[] RenderToBgra(int pixelWidth, int pixelHeight, bool renderFormFields = true)
        => RenderToBgraCore(pixelWidth, pixelHeight, renderFormFields, formHandleForLiveOverlay: IntPtr.Zero);

    /// <summary>Same as <see cref="RenderToBgra"/>, plus <c>FPDF_FFLDraw</c> to overlay the live interactive form-fill state - see <see cref="Forms.PdfFormFiller.RenderPageToBgra"/>, the only intended caller.</summary>
    internal byte[] RenderWithFormOverlay(int pixelWidth, int pixelHeight, IntPtr formHandle)
        => RenderToBgraCore(pixelWidth, pixelHeight, renderFormFields: true, formHandleForLiveOverlay: formHandle);

    private byte[] RenderToBgraCore(int pixelWidth, int pixelHeight, bool renderFormFields, IntPtr formHandleForLiveOverlay)
    {
        if (pixelWidth <= 0 || pixelHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pixelWidth), "Le dimensioni di rendering devono essere positive.");
        }

        var stride = pixelWidth * 4;
        var buffer = new byte[stride * pixelHeight];
        var bufferHandle = System.Runtime.InteropServices.GCHandle.Alloc(buffer, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            // Routed through the dedicated PDFium thread for the whole native call sequence,
            // not just individual P/Invokes: PDFium (built here without its optional
            // multi-threading support) is not safe to call into from more than one thread at
            // a time, even for unrelated pages/documents - see PdfiumRuntime's remarks.
            PdfiumRuntime.Invoke(() =>
            {
                ThrowIfDisposed();

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

                    if (formHandleForLiveOverlay != IntPtr.Zero)
                    {
                        PdfiumNative.FPDF_FFLDraw(formHandleForLiveOverlay, bitmap, _handle, 0, 0, pixelWidth, pixelHeight, /* rotate */ 0, flags);
                    }
                }
                finally
                {
                    PdfiumNative.FPDFBitmap_Destroy(bitmap);
                }
            });
        }
        finally
        {
            bufferHandle.Free();
        }

        return buffer;
    }

    /// <summary>
    /// Draws this page's annotations and form field appearances directly into the page
    /// content and removes them as separate objects - the values a user (or
    /// <see cref="Forms.PdfFormFiller"/>) set become permanent, non-interactive page content.
    /// Typically the last step before <see cref="PdfDocument.SaveAs"/> when delivering a
    /// filled-in form. Does not require a form-fill environment - it operates directly on
    /// the page's own annotation appearance streams.
    /// </summary>
    /// <param name="forPrint">Selects the print-oriented vs. normal-display appearance where a field has both.</param>
    /// <returns><c>false</c> if there was nothing to flatten (e.g. no annotations/form fields on the page) - not an error.</returns>
    public bool Flatten(bool forPrint = false)
        => PdfiumRuntime.Invoke(() =>
        {
            ThrowIfDisposed();
            var flag = forPrint ? PdfiumNative.FLAT_PRINT : PdfiumNative.FLAT_NORMALDISPLAY;
            var result = PdfiumNative.FPDFPage_Flatten(_handle, flag);
            return result switch
            {
                PdfiumNative.FLATTEN_SUCCESS => true,
                PdfiumNative.FLATTEN_NOTHINGTODO => false,
                _ => throw new PdfException(PdfErrorCode.Unknown, $"Impossibile appiattire la pagina {Index}."),
            };
        });

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
        => PdfiumRuntime.Invoke(() =>
        {
            if (_disposed)
            {
                return;
            }

            PdfiumNative.FPDF_ClosePage(_handle);
            _disposed = true;
        });
}
