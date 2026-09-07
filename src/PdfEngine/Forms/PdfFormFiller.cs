using System.Runtime.InteropServices;
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

/// <param name="IsChecked">Only meaningful for <see cref="PdfFormFieldType.CheckBox"/>/<see cref="PdfFormFieldType.RadioButton"/> - <c>false</c> for every other type.</param>
public sealed record PdfFormField(int PageIndex, int AnnotIndex, string Name, PdfFormFieldType Type, string Value, bool IsChecked);

/// <summary>
/// AcroForm field read/write access. Reading field values/names only requires a form-fill
/// environment handle, not page rendering, so <see cref="ListFields"/> works with the same
/// environment as the write path below.
/// </summary>
/// <remarks>
/// PDFium has no direct "set field value" call - writing goes through the same interactive
/// path a real user driving the mouse/keyboard would: <see cref="SetFieldValue"/> focuses the
/// field's widget then selects-all-and-replaces its text; <see cref="SetChecked"/> simulates
/// a mouse click on the widget's own rectangle, letting PDFium's internal AcroForm handling
/// toggle it (and, for radio buttons, uncheck any sibling in the same group). Both need a
/// <c>FPDF_FORMFILLINFO</c> with real callbacks wired up (not the null stand-ins a read-only
/// environment can get away with), because this path calls back into <c>FFI_Invalidate</c>
/// and friends. The exact struct layout and which callbacks are actually required was
/// verified against the real <c>public/fpdf_formfill.h</c> for the exact
/// <c>bblanchon.PDFium.Win32</c> version this project references (see
/// docs/ARCHITECTURE.md "AcroForm filling") - re-verify there if that package version changes.
/// </remarks>
public sealed class PdfFormFiller : IDisposable
{
    private readonly PdfDocument _document;
    private PdfiumNative.FPDF_FORMFILLINFO _formInfo;
    private readonly IntPtr _formHandle;
    private bool _disposed;

    // Kept alive for the lifetime of this instance: once converted to a function pointer via
    // Marshal.GetFunctionPointerForDelegate, the delegate object itself must not be
    // GC'd for as long as native code might still call through that pointer (the struct
    // holding the pointers must anyway remain valid until FPDFDOC_ExitFormFillEnvironment,
    // per the header's own contract on FPDFDOC_InitFormFillEnvironment).
    private readonly PdfiumNative.FFI_InvalidateDelegate _invalidate = (_, _, _, _, _, _) => { };
    private readonly PdfiumNative.FFI_SetCursorDelegate _setCursor = (_, _) => { };
    private readonly PdfiumNative.FFI_SetTimerDelegate _setTimer = (_, _, _) => 1;
    private readonly PdfiumNative.FFI_KillTimerDelegate _killTimer = (_, _) => { };
    private readonly PdfiumNative.FFI_ExecuteNamedActionDelegate _executeNamedAction = (_, _) => { };
    private readonly PdfiumNative.FFI_GetPageDelegate _getPage;

    // Set immediately before an interactive operation (SetFieldValue/SetChecked) so the
    // FFI_GetPage callback above can answer PDFium if it asks for "the page at this index"
    // mid-operation - both operations only ever touch one page at a time, so this simple
    // single-slot cache (instead of tracking every page the caller has ever opened) is enough.
    private IntPtr _activePageHandle = IntPtr.Zero;
    private int _activePageIndex = -1;

    public PdfFormFiller(PdfDocument document)
    {
        _document = document ?? throw new ArgumentNullException(nameof(document));
        _getPage = GetPageCallback;

        // Non-XFA builds (bblanchon.PDFium.Win32) require version = 1.
        _formInfo = new PdfiumNative.FPDF_FORMFILLINFO
        {
            version = 1,
            FFI_Invalidate = Marshal.GetFunctionPointerForDelegate(_invalidate),
            FFI_SetCursor = Marshal.GetFunctionPointerForDelegate(_setCursor),
            FFI_SetTimer = Marshal.GetFunctionPointerForDelegate(_setTimer),
            FFI_KillTimer = Marshal.GetFunctionPointerForDelegate(_killTimer),
            FFI_GetPage = Marshal.GetFunctionPointerForDelegate(_getPage),
            FFI_ExecuteNamedAction = Marshal.GetFunctionPointerForDelegate(_executeNamedAction),
        };

        lock (PdfiumRuntime.Gate)
        {
            _formHandle = PdfiumNative.FPDFDOC_InitFormFillEnvironment(document.Handle, ref _formInfo);
        }

        if (_formHandle == IntPtr.Zero)
        {
            throw new PdfException(PdfErrorCode.Unknown, "Impossibile inizializzare l'ambiente dei moduli PDF (il documento non contiene un AcroForm?).");
        }
    }

    private IntPtr GetPageCallback(IntPtr pThis, IntPtr document, int pageIndex)
        => pageIndex == _activePageIndex ? _activePageHandle : IntPtr.Zero;

    /// <summary>
    /// Renders a page the same way <see cref="PdfPage.RenderToBgra"/> does, but additionally
    /// overlays the live interactive form-fill state (<c>FPDF_FFLDraw</c>). Use this instead
    /// of <see cref="PdfPage.RenderToBgra"/> whenever a <see cref="PdfFormFiller"/> is open on
    /// the document, so a value just changed via <see cref="SetFieldValue"/>/<see cref="SetChecked"/>
    /// (and not yet flattened) actually shows up - plain <c>FPDF_RenderPageBitmap</c> only
    /// paints each widget's last-generated appearance stream, which a value change does not
    /// regenerate on its own.
    /// </summary>
    public byte[] RenderPageToBgra(int pageIndex, int pixelWidth, int pixelHeight)
    {
        ThrowIfDisposed();
        lock (PdfiumRuntime.Gate)
        {
            using var page = _document.GetPage(pageIndex);

            // Same requirement as the interactive calls in RunOnFieldAnnot: FPDF_FFLDraw
            // needs PDFium's internal page-view state for this exact page handle, not just
            // the one that existed (and was torn down again) while the field was being
            // edited.
            PdfiumNative.FORM_OnAfterLoadPage(page.Handle, _formHandle);
            try
            {
                return page.RenderWithFormOverlay(pixelWidth, pixelHeight, _formHandle);
            }
            finally
            {
                PdfiumNative.FORM_OnBeforeClosePage(page.Handle, _formHandle);
            }
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
                        var isChecked = type is PdfFormFieldType.CheckBox or PdfFormFieldType.RadioButton
                            && PdfiumNative.FPDFAnnot_IsChecked(_formHandle, annot);

                        fields.Add(new PdfFormField(pageIndex, annotIndex, name, type, value, isChecked));
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

    /// <summary>
    /// Sets the text of a <see cref="PdfFormFieldType.Text"/> field (focus, select-all,
    /// replace-selection, then kill focus to commit the value - the same sequence a user
    /// tabbing in, retyping, and tabbing back out would produce).
    /// </summary>
    public void SetFieldValue(PdfFormField field, string newValue)
    {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(newValue);
        ThrowIfDisposed();

        if (field.Type != PdfFormFieldType.Text)
        {
            throw new NotSupportedException(
                $"SetFieldValue supporta solo campi di testo. Il campo '{field.Name}' è di tipo {field.Type}: usare SetChecked per CheckBox/RadioButton.");
        }

        RunOnFieldAnnot(field, (page, annot) =>
        {
            if (!PdfiumNative.FORM_SetFocusedAnnot(_formHandle, annot))
            {
                throw new PdfException(PdfErrorCode.Unknown, $"Impossibile impostare il focus sul campo '{field.Name}'.");
            }

            PdfiumNative.FORM_SelectAllText(_formHandle, page);
            PdfiumNative.FORM_ReplaceSelection(_formHandle, page, newValue);
            PdfiumNative.FORM_ForceToKillFocus(_formHandle);
        });
    }

    /// <summary>
    /// Checks/unchecks a <see cref="PdfFormFieldType.CheckBox"/> or selects a
    /// <see cref="PdfFormFieldType.RadioButton"/> (pass <c>true</c>) by simulating a mouse
    /// click at the center of the widget's own rectangle - see the type-level remarks for why.
    /// A no-op if the field is already in the requested state (clicking an already-checked
    /// checkbox would otherwise toggle it back off).
    /// </summary>
    public void SetChecked(PdfFormField field, bool isChecked)
    {
        ArgumentNullException.ThrowIfNull(field);
        ThrowIfDisposed();

        if (field.Type is not (PdfFormFieldType.CheckBox or PdfFormFieldType.RadioButton))
        {
            throw new NotSupportedException(
                $"SetChecked supporta solo CheckBox/RadioButton. Il campo '{field.Name}' è di tipo {field.Type}: usare SetFieldValue per i campi di testo.");
        }

        RunOnFieldAnnot(field, (page, annot) =>
        {
            if (PdfiumNative.FPDFAnnot_IsChecked(_formHandle, annot) == isChecked)
            {
                return;
            }

            var rect = new PdfiumNative.FS_RECTF();
            if (!PdfiumNative.FPDFAnnot_GetRect(annot, ref rect))
            {
                throw new PdfException(PdfErrorCode.Unknown, $"Impossibile leggere l'area del campo '{field.Name}'.");
            }

            var x = (rect.Left + rect.Right) / 2.0;
            var y = (rect.Top + rect.Bottom) / 2.0;

            PdfiumNative.FORM_OnLButtonDown(_formHandle, page, 0, x, y);
            PdfiumNative.FORM_OnLButtonUp(_formHandle, page, 0, x, y);
        });
    }

    /// <summary>Opens the field's page/annotation, points the FFI_GetPage callback at it, runs <paramref name="action"/>, then cleans up - shared by <see cref="SetFieldValue"/> and <see cref="SetChecked"/>.</summary>
    private void RunOnFieldAnnot(PdfFormField field, Action<IntPtr, IntPtr> action)
    {
        lock (PdfiumRuntime.Gate)
        {
            using var page = _document.GetPage(field.PageIndex);
            _activePageHandle = page.Handle;
            _activePageIndex = field.PageIndex;

            // Required before any interactive form call (FORM_SetFocusedAnnot,
            // FORM_OnLButtonDown/Up, ...): it creates PDFium's internal page-view state for
            // this page. Without it those calls fail cleanly (return false) rather than
            // crash, but they do fail - confirmed while testing this against a real form PDF.
            PdfiumNative.FORM_OnAfterLoadPage(page.Handle, _formHandle);

            try
            {
                var annot = PdfiumNative.FPDFPage_GetAnnot(page.Handle, field.AnnotIndex);
                if (annot == IntPtr.Zero)
                {
                    throw new PdfException(PdfErrorCode.Unknown, $"Impossibile trovare l'annotazione per il campo '{field.Name}'.");
                }

                try
                {
                    action(page.Handle, annot);
                }
                finally
                {
                    PdfiumNative.FPDFPage_CloseAnnot(annot);
                }
            }
            finally
            {
                PdfiumNative.FORM_OnBeforeClosePage(page.Handle, _formHandle);
                _activePageHandle = IntPtr.Zero;
                _activePageIndex = -1;
            }
        }
    }

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
