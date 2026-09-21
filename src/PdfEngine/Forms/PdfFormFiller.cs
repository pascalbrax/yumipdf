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
/// <param name="IsMultiline">Only meaningful for <see cref="PdfFormFieldType.Text"/> - the AcroForm "Multiline" flag (bit 13 of /Ff), so a caller's text editor can accept Enter/wrap instead of treating the field as single-line.</param>
/// <param name="RectLeft">Widget rectangle in PDF points (1/72 inch, origin bottom-left of the page) - lets a caller draw an on-page overlay (highlight frame, in-place editor) at the field's exact position.</param>
public sealed record PdfFormField(
    int PageIndex, int AnnotIndex, string Name, PdfFormFieldType Type, string Value, bool IsChecked, bool IsMultiline,
    float RectLeft, float RectBottom, float RectRight, float RectTop);

/// <summary>
/// AcroForm field read/write access. Reading field values/names only requires a form-fill
/// environment handle, not page rendering, so <see cref="ListFields"/> works with the same
/// environment as the write path below.
/// </summary>
/// <remarks>
/// PDFium has no direct "set field value" call - writing goes through the same interactive
/// path a real user driving the mouse/keyboard would: <see cref="SetFieldValueAndRender"/>
/// focuses the field's widget then selects-all-and-replaces its text; <see cref="SetCheckedAndRender"/>
/// simulates a mouse click on the widget's own rectangle, letting PDFium's internal AcroForm
/// handling toggle it (and, for radio buttons, uncheck any sibling in the same group). Both
/// need a <c>FPDF_FORMFILLINFO</c> with real callbacks wired up (not the null stand-ins a
/// read-only environment can get away with), because this path calls back into
/// <c>FFI_Invalidate</c> and friends. The exact struct layout and which callbacks are actually
/// required was verified against the real <c>public/fpdf_formfill.h</c> for the exact
/// <c>bblanchon.PDFium.Win32</c> version this project references (see
/// docs/ARCHITECTURE.md "AcroForm filling") - re-verify there if that package version changes.
/// </remarks>
public sealed class PdfFormFiller : IDisposable
{
    private readonly PdfDocument _document;

    // Allocated on the unmanaged heap, not a plain struct field: PDFium keeps this address (as
    // "pThis") for the lifetime of the form-fill environment and hands it back on every later
    // callback - a byref/pinned struct field on a managed object is only guaranteed stable for
    // the duration of the one P/Invoke call that passes it, and a later compacting GC is free
    // to relocate the containing object (this PdfFormFiller instance). That relocation was
    // confirmed as the actual root cause of a crash chased for a long time: the app reliably
    // died a few fields into editing a real form, always inside FORM_SetFocusedAnnot. Two other
    // real bugs (page-view churn in EnsurePageOpen, and a FORM_ReplaceSelection string only
    // pinned for the one call in SetFieldValueAndRender) were found and fixed along the way and
    // are worth keeping regardless, but neither explained the crash on their own - it was only
    // forcing a GC after every edit in an isolated PdfEngine-only console repro (which otherwise
    // never crashed across all 33 fields of a real form) that reproduced it outside the app too,
    // pointing at GC relocation rather than anything specific to WinUI3. Allocating this struct
    // on the unmanaged heap instead removes it from the GC's control entirely, for as long as
    // this instance exists.
    private readonly IntPtr _formInfoPtr;
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

    // Every native string buffer handed to FORM_ReplaceSelection so far, kept alive until
    // Dispose - see SetFieldValueAndRender's remarks for why they cannot be freed right after
    // the call that used them.
    private readonly List<IntPtr> _replaceSelectionBuffers = new();

    // Every annotation handle opened on _openPage so far, kept open (never individually
    // closed) until the whole page-view closes - see EnsureAnnotOpen's remarks for why this
    // has to hold onto all of them, not just the most recently used one.
    private readonly Dictionary<int, IntPtr> _openAnnotsByIndex = new();

    public PdfFormFiller(PdfDocument document)
    {
        _document = document ?? throw new ArgumentNullException(nameof(document));
        _getPage = GetPageCallback;

        // Non-XFA builds (bblanchon.PDFium.Win32) require version = 1.
        var formInfo = new PdfiumNative.FPDF_FORMFILLINFO
        {
            version = 1,
            FFI_Invalidate = Marshal.GetFunctionPointerForDelegate(_invalidate),
            FFI_SetCursor = Marshal.GetFunctionPointerForDelegate(_setCursor),
            FFI_SetTimer = Marshal.GetFunctionPointerForDelegate(_setTimer),
            FFI_KillTimer = Marshal.GetFunctionPointerForDelegate(_killTimer),
            FFI_GetPage = Marshal.GetFunctionPointerForDelegate(_getPage),
            FFI_ExecuteNamedAction = Marshal.GetFunctionPointerForDelegate(_executeNamedAction),
        };

        _formInfoPtr = Marshal.AllocHGlobal(Marshal.SizeOf<PdfiumNative.FPDF_FORMFILLINFO>());
        Marshal.StructureToPtr(formInfo, _formInfoPtr, fDeleteOld: false);

        _formHandle = PdfiumRuntime.Invoke(() => PdfiumNative.FPDFDOC_InitFormFillEnvironment(document.Handle, _formInfoPtr));

        if (_formHandle == IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_formInfoPtr);
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
    /// just been focused and edited. That repeated churn was a real, independent bug (fixed
    /// here), though it turned out not to be the whole story - see the type-level remarks on
    /// <see cref="_formInfoPtr"/> for what the actual remaining crash was. Keeping the
    /// page-view open across consecutive operations on the same page - closing it only when
    /// the caller actually moves to a different page, or on <see cref="Dispose"/> - removes
    /// that open/close churn.
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

        // Every open annotation handle belongs to this page - none of them can outlive it.
        CloseAllAnnots();

        PdfiumNative.FORM_OnBeforeClosePage(_openPage.Handle, _formHandle);
        _openPage.Dispose();
        _openPage = null;
        _openPageIndex = -1;
    }

    /// <summary>
    /// Makes <paramref name="field"/>'s annotation handle open on <paramref name="page"/>,
    /// reusing an already-open handle for it if there is one.
    /// </summary>
    /// <remarks>
    /// Closing (<c>FPDFPage_CloseAnnot</c>) each field's handle - either right after editing
    /// it, or when the caller moved on to the next field - was a real, independent bug too: a
    /// per-native-call trace log (added to chase the crash down after the page-view fix alone
    /// did not fully explain it) caught <c>FPDFPage_GetAnnot</c> returning the exact same
    /// pointer value a just-closed field's handle had held, for a completely different field -
    /// PDFium had reused that freed memory, meaning something internal was left referencing
    /// the closed handle (that <c>FORM_ForceToKillFocus</c> evidently does not fully clear) and
    /// then got dereferenced as if it were the new, unrelated object. Never closing a field's
    /// annotation handle once opened - closing them all together only when the whole page-view
    /// closes, via <see cref="CloseAllAnnots"/> - removes that reuse. See the type-level
    /// remarks on <see cref="_formInfoPtr"/> for the deeper bug this one was standing in front of.
    /// </remarks>
    private IntPtr EnsureAnnotOpen(PdfPage page, PdfFormField field)
    {
        if (_openAnnotsByIndex.TryGetValue(field.AnnotIndex, out var existing))
        {
            return existing;
        }

        var annot = PdfiumNative.FPDFPage_GetAnnot(page.Handle, field.AnnotIndex);
        if (annot == IntPtr.Zero)
        {
            throw new PdfException(PdfErrorCode.Unknown, $"Impossibile trovare l'annotazione per il campo '{field.Name}'.");
        }

        _openAnnotsByIndex[field.AnnotIndex] = annot;
        return annot;
    }

    private void CloseAllAnnots()
    {
        foreach (var annot in _openAnnotsByIndex.Values)
        {
            PdfiumNative.FPDFPage_CloseAnnot(annot);
        }

        _openAnnotsByIndex.Clear();
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

    /// <summary>Lists a <see cref="PdfFormFieldType.ComboBox"/>/<see cref="PdfFormFieldType.ListBox"/> field's selectable option labels, in order.</summary>
    public IReadOnlyList<string> GetOptions(PdfFormField field)
    {
        ThrowIfDisposed();
        return PdfiumRuntime.Invoke(() =>
        {
            var page = EnsurePageOpen(field.PageIndex);
            var annot = EnsureAnnotOpen(page, field);

            var count = PdfiumNative.FPDFAnnot_GetOptionCount(_formHandle, annot);
            var options = new List<string>(Math.Max(0, count));
            for (var i = 0; i < count; i++)
            {
                var index = i;
                options.Add(ReadUtf16String((buf, len) => PdfiumNative.FPDFAnnot_GetOptionLabel(_formHandle, annot, index, buf, len)));
            }

            return options;
        });
    }

    /// <summary>The 0-based indices currently selected in a <see cref="PdfFormFieldType.ComboBox"/>/<see cref="PdfFormFieldType.ListBox"/> field (0 or 1 entries for a combo box, 0+ for a multi-select list box).</summary>
    public IReadOnlyList<int> GetSelectedOptionIndices(PdfFormField field)
    {
        ThrowIfDisposed();
        return PdfiumRuntime.Invoke(() =>
        {
            var page = EnsurePageOpen(field.PageIndex);
            var annot = EnsureAnnotOpen(page, field);

            var count = PdfiumNative.FPDFAnnot_GetOptionCount(_formHandle, annot);
            var selected = new List<int>();
            for (var i = 0; i < count; i++)
            {
                if (PdfiumNative.FPDFAnnot_IsOptionSelected(_formHandle, annot, i))
                {
                    selected.Add(i);
                }
            }

            return selected;
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
                        var isMultiline = type == PdfFormFieldType.Text
                            && (PdfiumNative.FPDFAnnot_GetFormFieldFlags(_formHandle, annot) & PdfiumNative.FormFieldFlagMultiline) != 0;

                        var rect = new PdfiumNative.FS_RECTF();
                        PdfiumNative.FPDFAnnot_GetRect(annot, ref rect);

                        fields.Add(new PdfFormField(
                            pageIndex, annotIndex, name, type, value, isChecked, isMultiline,
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
    /// <remarks>
    /// <c>newValue</c> is copied into a manually-allocated native buffer (kept alive, not freed,
    /// until this instance is disposed) instead of being passed as an auto-marshaled
    /// <c>[MarshalAs(LPWStr)] string</c>: the interop marshaler only pins a string argument for
    /// the duration of the one P/Invoke call, and there was no way to be sure PDFium never reads
    /// this pointer back afterwards. That turned out to be a real, independent bug worth fixing
    /// regardless, though - like the page-view and annotation-handle lifetime fixes elsewhere in
    /// this type - it was not the actual root cause of the crash this file's remarks describe;
    /// see <see cref="_formInfoPtr"/>'s remarks for that.
    /// </remarks>
    public byte[] SetFieldValueAndRender(PdfFormField field, string newValue, int pixelWidth, int pixelHeight)
    {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(newValue);
        ThrowIfDisposed();

        if (field.Type != PdfFormFieldType.Text)
        {
            throw new NotSupportedException(
                $"SetFieldValueAndRender supporta solo campi di testo. Il campo '{field.Name}' è di tipo {field.Type}: usare SetCheckedAndRender per CheckBox/RadioButton, SetComboSelectionAndRender per ComboBox.");
        }

        return ReplaceTextAndRender(field, newValue, pixelWidth, pixelHeight);
    }

    /// <summary>
    /// Selects the option at <paramref name="optionIndex"/> (an index into <see cref="GetOptions"/>'s
    /// ordered label list) in a <see cref="PdfFormFieldType.ComboBox"/>.
    /// </summary>
    /// <remarks>
    /// Goes through <c>FORM_SetIndexSelected</c>, not through the text-replacement sequence
    /// <see cref="SetFieldValueAndRender"/> uses. The text route was tried first and looked fine
    /// live, but it only ever worked for the *first* selection of an empty combo box: once the
    /// field already had a value, replacing its text was silently ignored (checked against two
    /// real forms - "Choice 1" -> "Choice 2" and "New York" -> "London" both left the old value
    /// in place), so a changed choice never reached the page, the printout or the saved file
    /// while the side panel happily showed the new one. <c>FORM_SetIndexSelected</c> applies
    /// every change, repeated ones included.
    /// </remarks>
    public byte[] SetComboSelectionAndRender(PdfFormField field, int optionIndex, int pixelWidth, int pixelHeight)
    {
        ArgumentNullException.ThrowIfNull(field);
        ThrowIfDisposed();

        if (field.Type != PdfFormFieldType.ComboBox)
        {
            throw new NotSupportedException(
                $"SetComboSelectionAndRender supporta solo ComboBox. Il campo '{field.Name}' è di tipo {field.Type}.");
        }

        return SelectIndexAndRender(field, optionIndex, pixelWidth, pixelHeight);
    }

    /// <summary>Shared by <see cref="SetComboSelectionAndRender"/> and <see cref="SetListSelectionAndRender"/>.</summary>
    private byte[] SelectIndexAndRender(PdfFormField field, int optionIndex, int pixelWidth, int pixelHeight)
        => RunOnFieldAnnotAndRender(field, pixelWidth, pixelHeight, (page, annot) =>
        {
            var count = PdfiumNative.FPDFAnnot_GetOptionCount(_formHandle, annot);
            if (optionIndex < 0 || optionIndex >= count)
            {
                throw new ArgumentOutOfRangeException(nameof(optionIndex));
            }

            if (!PdfiumNative.FORM_SetFocusedAnnot(_formHandle, annot))
            {
                throw new PdfException(PdfErrorCode.Unknown, $"Impossibile impostare il focus sul campo '{field.Name}'.");
            }

            bool selected;
            try
            {
                // FORM_SetIndexSelected only ever *adds* to a multi-select list box's selection
                // (caught live: picking French, German, English in turn left all three highlighted
                // on the page while the single-selection side panel showed one). The caller's model
                // is "this one option", so clear whatever else is selected first.
                for (var i = 0; i < count; i++)
                {
                    if (i != optionIndex && PdfiumNative.FORM_IsIndexSelected(_formHandle, page, i))
                    {
                        PdfiumNative.FORM_SetIndexSelected(_formHandle, page, i, false);
                    }
                }

                selected = PdfiumNative.FORM_SetIndexSelected(_formHandle, page, optionIndex, true);
            }
            finally
            {
                // The explicit commit step every interactive edit needs (see ReplaceTextAndRender).
                PdfiumNative.FORM_ForceToKillFocus(_formHandle);
            }

            if (!selected)
            {
                throw new PdfException(PdfErrorCode.Unknown, $"Impossibile selezionare la voce {optionIndex} del campo '{field.Name}'.");
            }
        });

    private byte[] ReplaceTextAndRender(PdfFormField field, string newValue, int pixelWidth, int pixelHeight)
        => RunOnFieldAnnotAndRender(field, pixelWidth, pixelHeight, (page, annot) =>
        {
            if (!PdfiumNative.FORM_SetFocusedAnnot(_formHandle, annot))
            {
                throw new PdfException(PdfErrorCode.Unknown, $"Impossibile impostare il focus sul campo '{field.Name}'.");
            }

            PdfiumNative.FORM_SelectAllText(_formHandle, page);

            var textBuffer = Marshal.StringToHGlobalUni(newValue);
            _replaceSelectionBuffers.Add(textBuffer);
            PdfiumNative.FORM_ReplaceSelection(_formHandle, page, textBuffer);
            PdfiumNative.FORM_ForceToKillFocus(_formHandle);
        });

    /// <summary>
    /// Selects the option at <paramref name="optionIndex"/> in a <see cref="PdfFormFieldType.ListBox"/>
    /// field (indices refer into <see cref="GetOptions"/>'s ordered label list).
    /// </summary>
    /// <remarks>
    /// Same <c>FORM_SetIndexSelected</c> route as <see cref="SetComboSelectionAndRender"/>. It
    /// replaced a simulated click at the row's computed position, which assumed every option is
    /// visible at once with the same height (<c>rowHeight = widgetHeight / optionCount</c>) and so
    /// picked the wrong row for a long, scrolling list.
    /// </remarks>
    public byte[] SetListSelectionAndRender(PdfFormField field, int optionIndex, int pixelWidth, int pixelHeight)
    {
        ArgumentNullException.ThrowIfNull(field);
        ThrowIfDisposed();

        if (field.Type != PdfFormFieldType.ListBox)
        {
            throw new NotSupportedException(
                $"SetListSelectionAndRender supporta solo ListBox. Il campo '{field.Name}' è di tipo {field.Type}.");
        }

        return SelectIndexAndRender(field, optionIndex, pixelWidth, pixelHeight);
    }

    /// <summary>
    /// Clicks a <see cref="PdfFormFieldType.PushButton"/> at the center of its own rectangle -
    /// whatever action is attached to it (a "Reset Form" action, most commonly) runs as part of
    /// PDFium's own click handling, the same as it would for a real user's mouse click.
    /// </summary>
    /// <remarks>
    /// A reset action can change *other* fields' values, not just this button's own (there is
    /// no such thing as a push button's "value") - callers must re-read every field afterwards
    /// (<see cref="ListFields"/>) rather than assuming only this one field could have changed.
    /// </remarks>
    public byte[] ClickButtonAndRender(PdfFormField field, int pixelWidth, int pixelHeight)
    {
        ArgumentNullException.ThrowIfNull(field);
        ThrowIfDisposed();

        if (field.Type != PdfFormFieldType.PushButton)
        {
            throw new NotSupportedException(
                $"ClickButtonAndRender supporta solo PushButton. Il campo '{field.Name}' è di tipo {field.Type}.");
        }

        return RunOnFieldAnnotAndRender(field, pixelWidth, pixelHeight, (page, annot) =>
        {
            var rect = new PdfiumNative.FS_RECTF();
            if (!PdfiumNative.FPDFAnnot_GetRect(annot, ref rect))
            {
                throw new PdfException(PdfErrorCode.Unknown, $"Impossibile leggere l'area del campo '{field.Name}'.");
            }

            var x = (rect.Left + rect.Right) / 2.0;
            var y = (rect.Top + rect.Bottom) / 2.0;

            PdfiumNative.FORM_OnMouseMove(_formHandle, page, 0, x, y);
            PdfiumNative.FORM_OnLButtonDown(_formHandle, page, 0, x, y);
            PdfiumNative.FORM_OnLButtonUp(_formHandle, page, 0, x, y);
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
    /// <remarks>
    /// Selecting a second option in a radio group did nothing - the first click (selecting
    /// option A) worked, but a second click selecting option B, in the same page-view session,
    /// left both options unselected or A still selected, depending on the exact sequence -
    /// confirmed live against a real form with a two-option radio group. <c>FORM_OnLButtonDown</c>/
    /// <c>Up</c> hit-test purely by (x, y): they take no annotation handle, so PDFium has to
    /// resolve "which widget is under this point" itself, and that resolution turned out to
    /// depend on <c>FORM_OnMouseMove</c> having been called first - without it, PDFium's
    /// internal hover/hit-test state apparently stayed pinned to wherever the *previous* click
    /// left it. A real user's mouse always generates move events before a click; simulating
    /// only the down/up pair skipped a step PDFium's interactive layer depends on.
    /// </remarks>
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

            PdfiumNative.FORM_OnMouseMove(_formHandle, page, 0, x, y);
            PdfiumNative.FORM_OnLButtonDown(_formHandle, page, 0, x, y);
            PdfiumNative.FORM_OnLButtonUp(_formHandle, page, 0, x, y);
        });
    }

    /// <summary>
    /// Opens (or reuses - see <see cref="EnsurePageOpen"/>) the field's page, runs
    /// <paramref name="edit"/> on its annotation, then renders the result - shared by
    /// <see cref="SetFieldValueAndRender"/> and <see cref="SetCheckedAndRender"/>.
    /// </summary>
    private byte[] RunOnFieldAnnotAndRender(PdfFormField field, int pixelWidth, int pixelHeight, Action<IntPtr, IntPtr> edit)
        => PdfiumRuntime.Invoke(() =>
        {
            var page = EnsurePageOpen(field.PageIndex);
            var annot = EnsureAnnotOpen(page, field);

            edit(page.Handle, annot);

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
            Marshal.FreeHGlobal(_formInfoPtr);

            foreach (var buffer in _replaceSelectionBuffers)
            {
                Marshal.FreeHGlobal(buffer);
            }

            _replaceSelectionBuffers.Clear();
            _disposed = true;
        });
}
