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
/// <param name="RectLeft">Widget rectangle in PDF points (1/72 inch, origin bottom-left of the page) - lets a caller draw an on-page overlay (highlight frame, in-place editor) at the field's exact position.</param>
public sealed record PdfFormField(
    int PageIndex, int AnnotIndex, string Name, PdfFormFieldType Type, string Value, bool IsChecked,
    float RectLeft, float RectBottom, float RectRight, float RectTop);

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

    // The one page currently under FORM_OnAfterLoadPage, kept open (not closed and reopened
    // for every single field edit) so the FFI_GetPage callback above can answer PDFium if it
    // asks for "the page at this index" mid-operation - see EnsurePageOpen's remarks for why
    // this single-slot cache exists at all, not just a plain "open, use, close" per call.
    private PdfPage? _openPage;
    private int _openPageIndex = -1;

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

        _formHandle = PdfiumRuntime.Invoke(() => PdfiumNative.FPDFDOC_InitFormFillEnvironment(document.Handle, ref _formInfo));

        if (_formHandle == IntPtr.Zero)
        {
            throw new PdfException(PdfErrorCode.Unknown, "Impossibile inizializzare l'ambiente dei moduli PDF (il documento non contiene un AcroForm?).");
        }
    }

    private IntPtr GetPageCallback(IntPtr pThis, IntPtr document, int pageIndex)
        => pageIndex == _openPageIndex ? _openPage?.Handle ?? IntPtr.Zero : IntPtr.Zero;

    /// <summary>
    /// Makes <paramref name="pageIndex"/> the one page under <c>FORM_OnAfterLoadPage</c>,
    /// reusing the already-open page-view if it is already this same page.
    /// </summary>
    /// <remarks>
    /// Every render/edit call used to open (<c>FPDF_LoadPage</c>) and close
    /// (<c>FPDF_ClosePage</c>) its own page handle, tearing down and rebuilding PDFium's
    /// internal page-view every single time - including right after a field on that page had
    /// just been focused and edited. That repeated churn corrupted the native heap
    /// (<c>System.ExecutionEngineException</c>, no managed stack) - confirmed via crash dumps
    /// captured while testing live against a real form: the app first died after 2-3 fields,
    /// and even after fusing one edit's own edit+render into a single page-view lifecycle (see
    /// <see cref="RunOnFieldAnnotAndRender"/>'s remarks) it still eventually died a few fields
    /// later, from the *next* field's edit reopening the page fresh. Keeping the page-view open
    /// across consecutive operations on the same page - closing it only when the caller
    /// actually moves to a different page, or on <see cref="Dispose"/> - removes that churn
    /// instead of just reducing it.
    /// </remarks>
    private PdfPage EnsurePageOpen(int pageIndex)
    {
        if (_openPage is not null && _openPageIndex == pageIndex)
        {
            return _openPage;
        }

        ClosePage();

        var page = _document.GetPage(pageIndex);
        PdfiumNative.FORM_OnAfterLoadPage(page.Handle, _formHandle);
        _openPage = page;
        _openPageIndex = pageIndex;
        return page;
    }

    private void ClosePage()
    {
        if (_openPage is null)
        {
            return;
        }

        PdfiumNative.FORM_OnBeforeClosePage(_openPage.Handle, _formHandle);
        _openPage.Dispose();
        _openPage = null;
        _openPageIndex = -1;
    }

    /// <summary>
    /// Renders a page the same way <see cref="PdfPage.RenderToBgra"/> does, but additionally
    /// overlays the live interactive form-fill state (<c>FPDF_FFLDraw</c>). Use this instead
    /// of <see cref="PdfPage.RenderToBgra"/> whenever a <see cref="PdfFormFiller"/> is open on
    /// the document, so a value just changed via <see cref="SetFieldValueAndRender"/>/<see cref="SetCheckedAndRender"/>
    /// (and not yet flattened) actually shows up - plain <c>FPDF_RenderPageBitmap</c> only
    /// paints each widget's last-generated appearance stream, which a value change does not
    /// regenerate on its own.
    /// </summary>
    public byte[] RenderPageToBgra(int pageIndex, int pixelWidth, int pixelHeight)
    {
        ThrowIfDisposed();
        return PdfiumRuntime.Invoke(() =>
        {
            var page = EnsurePageOpen(pageIndex);
            return page.RenderWithFormOverlay(pixelWidth, pixelHeight, _formHandle);
        });
    }

    /// <summary>Enumerates AcroForm widget fields across every page.</summary>
    public IReadOnlyList<PdfFormField> ListFields()
    {
        ThrowIfDisposed();
        var fields = new List<PdfFormField>();

        PdfiumRuntime.Invoke(() =>
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

                        var rect = new PdfiumNative.FS_RECTF();
                        PdfiumNative.FPDFAnnot_GetRect(annot, ref rect);

                        fields.Add(new PdfFormField(
                            pageIndex, annotIndex, name, type, value, isChecked,
                            rect.Left, rect.Bottom, rect.Right, rect.Top));
                    }
                    finally
                    {
                        PdfiumNative.FPDFPage_CloseAnnot(annot);
                    }
                }
            }
        });

        return fields;
    }

    /// <summary>
    /// Sets the text of a <see cref="PdfFormFieldType.Text"/> field (focus, select-all,
    /// replace-selection, then kill focus to commit the value - the same sequence a user
    /// tabbing in, retyping, and tabbing back out would produce) and immediately renders the
    /// page's new state - see <see cref="RunOnFieldAnnotAndRender"/> for why the render has to
    /// happen in the same call rather than as a separate follow-up render.
    /// </summary>
    public byte[] SetFieldValueAndRender(PdfFormField field, string newValue, int pixelWidth, int pixelHeight)
    {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(newValue);
        ThrowIfDisposed();

        if (field.Type != PdfFormFieldType.Text)
        {
            throw new NotSupportedException(
                $"SetFieldValueAndRender supporta solo campi di testo. Il campo '{field.Name}' è di tipo {field.Type}: usare SetCheckedAndRender per CheckBox/RadioButton.");
        }

        return RunOnFieldAnnotAndRender(field, pixelWidth, pixelHeight, (page, annot) =>
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
    /// click at the center of the widget's own rectangle - see the type-level remarks for why -
    /// and immediately renders the page's new state (see <see cref="RunOnFieldAnnotAndRender"/>).
    /// A no-op edit (but still rendered) if the field is already in the requested state
    /// (clicking an already-checked checkbox would otherwise toggle it back off).
    /// </summary>
    public byte[] SetCheckedAndRender(PdfFormField field, bool isChecked, int pixelWidth, int pixelHeight)
    {
        ArgumentNullException.ThrowIfNull(field);
        ThrowIfDisposed();

        if (field.Type is not (PdfFormFieldType.CheckBox or PdfFormFieldType.RadioButton))
        {
            throw new NotSupportedException(
                $"SetCheckedAndRender supporta solo CheckBox/RadioButton. Il campo '{field.Name}' è di tipo {field.Type}: usare SetFieldValueAndRender per i campi di testo.");
        }

        return RunOnFieldAnnotAndRender(field, pixelWidth, pixelHeight, (page, annot) =>
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

    /// <summary>
    /// Opens (or reuses - see <see cref="EnsurePageOpen"/>) the field's page, runs
    /// <paramref name="edit"/> on its annotation, then renders the result - shared by
    /// <see cref="SetFieldValueAndRender"/> and <see cref="SetCheckedAndRender"/>.
    /// </summary>
    /// <remarks>
    /// The edit and the render used to be two separate calls, each opening (<c>FPDF_LoadPage</c>)
    /// and closing (<c>FPDF_ClosePage</c>) its own page handle around its own
    /// <c>FORM_OnAfterLoadPage</c>/<c>FORM_OnBeforeClosePage</c> pair for the same page index -
    /// editing a field, tearing down that page-view, then immediately rebuilding a fresh one
    /// just to render. That tear-down/rebuild was not safe: PDFium's interactive form layer
    /// keeps state (e.g. the just-focused annotation <c>FORM_ForceToKillFocus</c> is supposed
    /// to have cleared) tied to the specific page-view instance, and closing it out from under
    /// a field that had just been focused corrupted the native heap - confirmed via a crash
    /// dump (<c>System.ExecutionEngineException</c>, no managed stack, on a thread sitting
    /// inside <c>FORM_SetFocusedAnnot</c> for the *next* field edit) when this was tested live
    /// against a real form. Fusing one edit's own edit+render into a single page-view lifecycle
    /// helped but did not fully fix it - the app still died a few fields later, from the next
    /// field's edit reopening the page fresh again. Routing through the shared
    /// <see cref="EnsurePageOpen"/> cache instead of opening/closing here directly removes that
    /// reopen entirely for consecutive edits on the same page.
    /// </remarks>
    private byte[] RunOnFieldAnnotAndRender(PdfFormField field, int pixelWidth, int pixelHeight, Action<IntPtr, IntPtr> edit)
        => PdfiumRuntime.Invoke(() =>
        {
            var page = EnsurePageOpen(field.PageIndex);

            var annot = PdfiumNative.FPDFPage_GetAnnot(page.Handle, field.AnnotIndex);
            if (annot == IntPtr.Zero)
            {
                throw new PdfException(PdfErrorCode.Unknown, $"Impossibile trovare l'annotazione per il campo '{field.Name}'.");
            }

            try
            {
                edit(page.Handle, annot);
            }
            finally
            {
                PdfiumNative.FPDFPage_CloseAnnot(annot);
            }

            return page.RenderWithFormOverlay(pixelWidth, pixelHeight, _formHandle);
        });

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
        => PdfiumRuntime.Invoke(() =>
        {
            if (_disposed)
            {
                return;
            }

            ClosePage();
            PdfiumNative.FPDFDOC_ExitFormFillEnvironment(_formHandle);
            _disposed = true;
        });
}
