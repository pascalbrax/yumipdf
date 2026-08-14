using System.Text;
using PdfEngine.Native;

namespace PdfEngine.Forms;

public enum PdfFormFieldType
{
    Unknown = 0,
    PushButton = 1,
    CheckBox = 2,
    RadioButton = 3,
    ComboBox = 4,
    ListBox = 5,
    Text = 6,
    Signature = 7,
}

public sealed record PdfFormField(int PageIndex, int AnnotIndex, string Name, PdfFormFieldType Type, string Value);

/// <summary>
/// AcroForm field access. Reading field values/names only requires a form-fill environment
/// handle, not page rendering, so <see cref="ListFields"/> is safe to call with a minimally
/// initialized <c>FPDF_FORMFILLINFO</c> (see <see cref="PdfiumNative.FPDF_FORMFILLINFO"/>).
/// </summary>
/// <remarks>
/// Writing field values (<c>FORM_SetFocusedAnnot</c> + <c>FORM_ReplaceSelection</c>) goes
/// through PDFium's interactive path, which calls back into <c>FFI_Invalidate</c> and
/// related redraw hooks. Wiring those up needs the exact, version-matched
/// <c>FPDF_FORMFILLINFO</c> layout to avoid corrupting the native call stack, so it is
/// intentionally deferred to roadmap milestone M2 (see docs/ROADMAP.md) rather than shipped
/// half-verified here - see <see cref="SetFieldValue"/>.
/// </remarks>
public sealed class PdfFormFiller : IDisposable
{
    private readonly PdfDocument _document;
    private PdfiumNative.FPDF_FORMFILLINFO _formInfo;
    private readonly IntPtr _formHandle;
    private bool _disposed;

    public PdfFormFiller(PdfDocument document)
    {
        _document = document ?? throw new ArgumentNullException(nameof(document));

        // Non-XFA builds (bblanchon.PDFium.Win32) require version = 1. All callbacks are
        // left null: safe for the read-only field-enumeration path below, which never
        // triggers PDFium's interactive/redraw callbacks. Do not call FORM_OnAfterLoadPage,
        // FPDF_FFLDraw, or any FORM_* interaction API against this handle.
        _formInfo = new PdfiumNative.FPDF_FORMFILLINFO { version = 1 };
        lock (PdfiumRuntime.Gate)
        {
            _formHandle = PdfiumNative.FPDFDOC_InitFormFillEnvironment(document.Handle, ref _formInfo);
        }

        if (_formHandle == IntPtr.Zero)
        {
            throw new PdfException(PdfErrorCode.Unknown, "Impossibile inizializzare l'ambiente dei moduli PDF (il documento non contiene un AcroForm?).");
        }
    }

    /// <summary>Enumerates AcroForm widget fields across every page.</summary>
    public IReadOnlyList<PdfFormField> ListFields()
    {
        ThrowIfDisposed();
        var fields = new List<PdfFormField>();

        lock (PdfiumRuntime.Gate)
        {
            for (var pageIndex = 0; pageIndex < _document.PageCount; pageIndex++)
            {
                using var page = _document.GetPage(pageIndex);
                var annotCount = PdfiumNative.FPDFPage_GetAnnotCount(page.Handle);

                for (var annotIndex = 0; annotIndex < annotCount; annotIndex++)
                {
                    var annot = PdfiumNative.FPDFPage_GetAnnot(page.Handle, annotIndex);
                    if (annot == IntPtr.Zero)
                    {
                        continue;
                    }

                    try
                    {
                        if (PdfiumNative.FPDFAnnot_GetSubtype(annot) != PdfiumNative.FPDF_ANNOTATION_SUBTYPE.WIDGET)
                        {
                            continue;
                        }

                        var type = (PdfFormFieldType)PdfiumNative.FPDFAnnot_GetFormFieldType(_formHandle, annot);
                        var name = ReadUtf16String((buf, len) => PdfiumNative.FPDFAnnot_GetFormFieldName(_formHandle, annot, buf, len));
                        var value = ReadUtf16String((buf, len) => PdfiumNative.FPDFAnnot_GetFormFieldValue(_formHandle, annot, buf, len));

                        fields.Add(new PdfFormField(pageIndex, annotIndex, name, type, value));
                    }
                    finally
                    {
                        PdfiumNative.FPDFPage_CloseAnnot(annot);
                    }
                }
            }
        }

        return fields;
    }

    /// <summary>Not yet implemented - see the remarks on <see cref="PdfFormFiller"/> and docs/ROADMAP.md milestone M2.</summary>
    public void SetFieldValue(PdfFormField field, string newValue)
        => throw new NotSupportedException(
            "La compilazione (scrittura) dei campi modulo è pianificata per la milestone M2: richiede il wiring completo di FPDF_FORMFILLINFO, non ancora verificato. Vedi docs/ROADMAP.md.");

    /// <summary>Two-pass PDFium string read: first call with a null buffer to get the required byte length (UTF-16LE, NUL-terminated).</summary>
    private static string ReadUtf16String(Func<byte[]?, uint, uint> reader)
    {
        var neededBytes = reader(null, 0);
        if (neededBytes <= 2)
        {
            return string.Empty;
        }

        var buffer = new byte[neededBytes];
        reader(buffer, neededBytes);
        // Drop the trailing UTF-16 NUL terminator before decoding.
        return Encoding.Unicode.GetString(buffer, 0, (int)neededBytes - 2);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(PdfFormFiller));
        }
    }

    public void Dispose()
    {
        lock (PdfiumRuntime.Gate)
        {
            if (_disposed)
            {
                return;
            }

            PdfiumNative.FPDFDOC_ExitFormFillEnvironment(_formHandle);
            _disposed = true;
        }
    }
}
