using System.Runtime.InteropServices;

namespace PdfEngine.Native;

/// <summary>
/// Raw P/Invoke surface for pdfium.dll (https://pdfium.googlesource.com/pdfium/,
/// prebuilt via https://github.com/bblanchon/pdfium-binaries, package
/// <c>bblanchon.PDFium.Win32</c>, Apache-2.0).
/// </summary>
/// <remarks>
/// PDFium's public headers define <c>FPDF_CALLCONV</c> as <c>__stdcall</c> on Win32 for
/// historical Acrobat-plugin ABI compatibility - every signature below must use
/// <see cref="CallingConvention.StdCall"/>, not the platform default.
/// Only the subset of the C API needed by <see cref="PdfEngine"/> is declared; extend
/// this file (matching the vendored <c>public/*.h</c> headers for the referenced
/// package version) rather than P/Invoking ad-hoc from elsewhere.
/// </remarks>
internal static class PdfiumNative
{
    private const string Lib = "pdfium";

    // ---- fpdfview.h : library lifecycle -----------------------------------------------

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern void FPDF_InitLibrary();

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern void FPDF_DestroyLibrary();

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern uint FPDF_GetLastError();

    // ---- fpdfview.h : document / page ---------------------------------------------------

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern IntPtr FPDF_LoadMemDocument64(byte[] dataBuf, long size, string? password);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern void FPDF_CloseDocument(IntPtr document);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern int FPDF_GetPageCount(IntPtr document);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern IntPtr FPDF_LoadPage(IntPtr document, int pageIndex);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern void FPDF_ClosePage(IntPtr page);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern double FPDF_GetPageWidth(IntPtr page);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern double FPDF_GetPageHeight(IntPtr page);

    // ---- fpdfview.h : bitmap rendering --------------------------------------------------

    public const int FPDFBitmap_BGRA = 4;
    public const int FPDF_ANNOT = 0x01;
    public const int FPDF_LCD_TEXT = 0x02;

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern IntPtr FPDFBitmap_CreateEx(
        int width, int height, int format, IntPtr firstScan, int stride);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern void FPDFBitmap_FillRect(
        IntPtr bitmap, int left, int top, int width, int height, uint color);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern void FPDF_RenderPageBitmap(
        IntPtr bitmap, IntPtr page, int startX, int startY, int sizeX, int sizeY,
        int rotate, int flags);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern void FPDFBitmap_Destroy(IntPtr bitmap);

    // ---- fpdf_save.h : saving ------------------------------------------------------------

    public const int FPDF_INCREMENTAL = 1;
    public const int FPDF_NO_INCREMENTAL = 2;
    public const int FPDF_REMOVE_SECURITY = 3;

    [StructLayout(LayoutKind.Sequential)]
    public struct FPDF_FILEWRITE
    {
        public int version;
        public WriteBlockDelegate WriteBlock;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate int WriteBlockDelegate(IntPtr pThis, IntPtr data, uint size);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern int FPDF_SaveAsCopy(IntPtr document, ref FPDF_FILEWRITE fileWrite, int flags);

    // ---- fpdf_ppo.h : page organizer (merge / split / reorder) -------------------------

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern int FPDF_ImportPagesByIndex(
        IntPtr destDocument, IntPtr srcDocument, int[] pageIndices, uint length, int insertIndex);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern IntPtr FPDF_CreateNewDocument();

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern void FPDFPage_Delete(IntPtr document, int pageIndex);

    // ---- fpdf_annot.h : markup annotations (highlight / note / ink / stamp) ------------

    public enum FPDF_ANNOTATION_SUBTYPE
    {
        UNKNOWN = 0,
        TEXT = 1,
        LINK = 2,
        FREETEXT = 3,
        LINE = 4,
        SQUARE = 5,
        CIRCLE = 6,
        POLYGON = 7,
        POLYLINE = 8,
        HIGHLIGHT = 9,
        UNDERLINE = 10,
        SQUIGGLY = 11,
        STRIKEOUT = 12,
        STAMP = 13,
        CARET = 14,
        INK = 15,
        POPUP = 16,
        FILEATTACHMENT = 17,
        SOUND = 18,
        MOVIE = 19,
        WIDGET = 20,
        SCREEN = 21,
        PRINTERMARK = 22,
        TRAPNET = 23,
        WATERMARK = 24,
        THREED = 25,
        RICHMEDIA = 26,
        XFAWIDGET = 27,
        REDACT = 28,
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct FS_RECTF
    {
        public float Left;
        public float Top;
        public float Right;
        public float Bottom;
    }

    public enum FPDFANNOT_COLORTYPE
    {
        Color = 0,
        InteriorColor = 1,
    }

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern int FPDFPage_GetAnnotCount(IntPtr page);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern IntPtr FPDFPage_GetAnnot(IntPtr page, int index);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern IntPtr FPDFPage_CreateAnnot(IntPtr page, FPDF_ANNOTATION_SUBTYPE subtype);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern void FPDFPage_CloseAnnot(IntPtr annot);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern bool FPDFAnnot_SetRect(IntPtr annot, ref FS_RECTF rect);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern bool FPDFAnnot_SetColor(
        IntPtr annot, FPDFANNOT_COLORTYPE type, uint r, uint g, uint b, uint a);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern FPDF_ANNOTATION_SUBTYPE FPDFAnnot_GetSubtype(IntPtr annot);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern bool FPDFAnnot_AppendAttachmentPoints(IntPtr annot, ref FS_QUADPOINTSF quadPoints);

    [StructLayout(LayoutKind.Sequential)]
    public struct FS_QUADPOINTSF
    {
        public float X1, Y1, X2, Y2, X3, Y3, X4, Y4;
    }

    // ---- fpdf_formfill.h : AcroForm field access ------------------------------------------
    // NOTE: FPDF_FORMFILLINFO's field layout below was verified against the actual vendored
    // public/fpdf_formfill.h header for bblanchon.PDFium.Win32 153.0.7999 (downloaded from
    // the matching pdfium-binaries GitHub release) - re-verify against that package's real
    // header again if its version ever changes; a mismatched struct layout corrupts the
    // native call stack instead of failing to compile. See docs/ARCHITECTURE.md "AcroForm
    // filling" for details and the verification method.

    // Callback delegate shapes actually wired up by PdfFormFiller (the "Implementation
    // Required: yes" callbacks for version 1, per the header's per-field comments - the rest
    // are documented as not required, or "Unused", and are left null).
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate void FFI_InvalidateDelegate(
        IntPtr pThis, IntPtr page, double left, double top, double right, double bottom);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate void FFI_SetCursorDelegate(IntPtr pThis, int nCursorType);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate int FFI_SetTimerDelegate(IntPtr pThis, int uElapse, IntPtr lpTimerFunc);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate void FFI_KillTimerDelegate(IntPtr pThis, int nTimerID);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate IntPtr FFI_GetPageDelegate(IntPtr pThis, IntPtr document, int nPageIndex);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate void FFI_ExecuteNamedActionDelegate(IntPtr pThis, IntPtr namedAction);
    [StructLayout(LayoutKind.Sequential)]
    public struct FPDF_FORMFILLINFO
    {
        public int version;
        public IntPtr Release;
        public IntPtr FFI_Invalidate;
        public IntPtr FFI_OutputSelectedRect;
        public IntPtr FFI_SetCursor;
        public IntPtr FFI_SetTimer;
        public IntPtr FFI_KillTimer;
        public IntPtr FFI_GetLocalTime;
        public IntPtr FFI_OnChange;
        public IntPtr FFI_GetPage;
        public IntPtr FFI_GetCurrentPage;
        public IntPtr FFI_GetRotation;
        public IntPtr FFI_ExecuteNamedAction;
        public IntPtr FFI_SetTextFieldFocus;
        public IntPtr FFI_DoURIAction;
        public IntPtr FFI_DoGoToAction;
        public IntPtr m_pJsPlatform;

        // Version 2 - experimental (ignored by PDFium when version == 1, which is what this
        // project uses, but included for an exact layout match with the real
        // public/fpdf_formfill.h - verified against the bblanchon.PDFium.Win32 153.0.7999
        // headers; re-check this block if that package version ever changes).
        public int xfa_disabled;
        public IntPtr FFI_DisplayCaret;
        public IntPtr FFI_GetCurrentPageIndex;
        public IntPtr FFI_SetCurrentPage;
        public IntPtr FFI_GotoURL;
        public IntPtr FFI_GetPageViewRect;
        public IntPtr FFI_PageEvent;
        public IntPtr FFI_PopupMenu;
        public IntPtr FFI_OpenFile;
        public IntPtr FFI_EmailTo;
        public IntPtr FFI_UploadTo;
        public IntPtr FFI_GetPlatform;
        public IntPtr FFI_GetLanguage;
        public IntPtr FFI_DownloadFromURL;
        public IntPtr FFI_PostRequestURL;
        public IntPtr FFI_PutRequestURL;
        public IntPtr FFI_OnFocusChange;
        public IntPtr FFI_DoURIActionWithKeyboardModifier;
    }

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    // IntPtr, not "ref FPDF_FORMFILLINFO": see PdfFormFiller's constructor remarks - PDFium
    // keeps this pointer (as "pThis") for the lifetime of the form-fill environment, handing
    // it back on every later callback, but a byref struct parameter is only pinned by the
    // interop marshaler for the duration of this one call - the caller has to give it a
    // stable, non-GC-moving (unmanaged) address of its own instead.
    public static extern IntPtr FPDFDOC_InitFormFillEnvironment(IntPtr document, IntPtr formInfo);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern void FPDFDOC_ExitFormFillEnvironment(IntPtr formHandle);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern int FPDFAnnot_GetFormFieldType(IntPtr formHandle, IntPtr annot);

    // Raw /Ff field flags (PDF 32000-1:2008 table 228/229). Bit 13 (1 << 12, value 4096) is
    // "Multiline" for text fields - the one flag this project currently reads.
    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern int FPDFAnnot_GetFormFieldFlags(IntPtr formHandle, IntPtr annot);

    public const int FormFieldFlagMultiline = 1 << 12;

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern uint FPDFAnnot_GetFormFieldName(
        IntPtr formHandle, IntPtr annot, byte[]? buffer, uint buflen);

    // ---- ComboBox/ListBox options (fpdf_annot.h) - read-only, annotation-level ------------
    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern int FPDFAnnot_GetOptionCount(IntPtr formHandle, IntPtr annot);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern uint FPDFAnnot_GetOptionLabel(
        IntPtr formHandle, IntPtr annot, int index, byte[]? buffer, uint buflen);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern bool FPDFAnnot_IsOptionSelected(IntPtr formHandle, IntPtr annot, int index);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern uint FPDFAnnot_GetFormFieldValue(
        IntPtr formHandle, IntPtr annot, byte[]? buffer, uint buflen);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern bool FORM_SetFocusedAnnot(IntPtr formHandle, IntPtr annot);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern bool FORM_SelectAllText(IntPtr formHandle, IntPtr page);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    // IntPtr, not a marshaled string: see PdfFormFiller.SetFieldValueAndRender's remarks - the
    // caller passes a manually-allocated, deliberately-not-immediately-freed buffer instead of
    // an auto-marshaled string, because PDFium reading it back after the call returns (found
    // live, via a GC-forcing repro) means the interop marshaler's own pin-for-the-call's-
    // duration lifetime is not long enough.
    public static extern void FORM_ReplaceSelection(IntPtr formHandle, IntPtr page, IntPtr text);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern void FORM_OnAfterLoadPage(IntPtr page, IntPtr formHandle);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern void FORM_OnBeforeClosePage(IntPtr page, IntPtr formHandle);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern bool FORM_ForceToKillFocus(IntPtr formHandle);

    // Selects an item of the *focused* combo box/list box (FORM_SetFocusedAnnot first). Exported by
    // pdfium.dll although easy to miss in the headers: for combo boxes it is the only route that
    // keeps working once the field already has a value - replacing its text (FORM_ReplaceSelection)
    // is silently ignored then. See PdfFormFiller.SetComboSelectionAndRender.
    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern bool FORM_SetIndexSelected(IntPtr formHandle, IntPtr page, int index, bool selected);

    // Live state of the focused combo box/list box - unlike FPDFAnnot_IsOptionSelected, which only
    // reflects a change once the focus has been released.
    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern bool FORM_IsIndexSelected(IntPtr formHandle, IntPtr page, int index);

    // FPDF_RenderPageBitmap only paints each widget's last-*generated* appearance stream; it
    // does not know a value was just changed interactively and does not regenerate it.
    // FPDF_FFLDraw overlays the live form-fill state on top - call it right after
    // FPDF_RenderPageBitmap (same bitmap/page/rect/rotate/flags) whenever the page might have
    // just-edited field values that have not been flattened yet.
    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern void FPDF_FFLDraw(
        IntPtr formHandle, IntPtr bitmap, IntPtr page, int startX, int startY, int sizeX, int sizeY, int rotate, int flags);

    // Checkbox/radio have no direct setter in the PDFium C API - the documented way to
    // change them is to simulate the mouse click a real user would make (PDFium's internal
    // AcroForm widget handling takes care of toggling the checked state, and - for radio
    // buttons - unchecking any sibling in the same group).
    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern bool FORM_OnMouseMove(IntPtr formHandle, IntPtr page, int modifier, double pageX, double pageY);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern bool FORM_OnLButtonDown(IntPtr formHandle, IntPtr page, int modifier, double pageX, double pageY);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern bool FORM_OnLButtonUp(IntPtr formHandle, IntPtr page, int modifier, double pageX, double pageY);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern bool FPDFAnnot_GetRect(IntPtr annot, ref FS_RECTF rect);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern bool FPDFAnnot_IsChecked(IntPtr formHandle, IntPtr annot);

    // ---- fpdf_flatten.h : flatten annotations/form fields into page content --------------

    public const int FLAT_NORMALDISPLAY = 0;
    public const int FLAT_PRINT = 1;

    public const int FLATTEN_FAIL = 0;
    public const int FLATTEN_SUCCESS = 1;
    public const int FLATTEN_NOTHINGTODO = 2;

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern int FPDFPage_Flatten(IntPtr page, int nFlag);

    // ---- fpdf_text.h : read-only text extraction from a page's own content stream --------
    // Reports only text the page already has (a born-digital PDF, or one an OCR pass already
    // wrote into) - a scanned page with no text layer reports 0 chars here. See
    // PdfPage.HasExtractableText/ExtractText, the only callers.

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern IntPtr FPDFText_LoadPage(IntPtr page);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern void FPDFText_ClosePage(IntPtr textPage);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern int FPDFText_CountChars(IntPtr textPage);

    // byte[] buffer, not char[]/string: FPDFText_GetText writes raw UTF-16LE code units, and
    // marshaling that as char[] with the default (Ansi) DllImport charset silently mangles it
    // (confirmed live: every letter came back with a spurious space after it) - decode with
    // Encoding.Unicode instead, same fix already needed once in the OCR sandwich spike this
    // binding grew out of.
    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern int FPDFText_GetText(IntPtr textPage, int startIndex, int count, [Out] byte[] buffer);

    // ---- fpdf_text.h : hit-testing a screen point to a character, for text selection ------
    // Read-only, like the find API below - maps a point in PDF point space to the character
    // index nearest it (within tolerance), or -1. See PdfPage.GetCharIndexAt/HitTestAndSelect,
    // the only callers - drag-to-select on a native-text page (PdfTextSelectionOverlay).

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern int FPDFText_GetCharIndexAtPos(
        IntPtr textPage, double x, double y, double xTolerance, double yTolerance);

    // ---- fpdf_text.h : find, for the search result highlight overlay ----------------------
    // Read-only and page-scoped, unlike the page-object *write* APIs (FPDFPageObj_NewTextObj
    // and friends) explored for the OCR "sandwich" - a real, unresolved rendering bug was
    // found there (see docs/ARCHITECTURE.md "OCR"). Nothing here touches page content.

    public const uint FPDF_MATCHCASE = 0x00000001;
    public const uint FPDF_MATCHWHOLEWORD = 0x00000002;

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern IntPtr FPDFText_FindStart(IntPtr textPage, IntPtr findWhat, uint flags, int startIndex);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern bool FPDFText_FindNext(IntPtr searchHandle);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern int FPDFText_GetSchResultIndex(IntPtr searchHandle);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern int FPDFText_GetSchCount(IntPtr searchHandle);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern void FPDFText_FindClose(IntPtr searchHandle);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern int FPDFText_CountRects(IntPtr textPage, int startIndex, int count);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern bool FPDFText_GetRect(IntPtr textPage, int rectIndex, out double left, out double top, out double right, out double bottom);

    // ---- fpdf_doc.h : bookmarks (the document outline) -----------------------------------
    // Read-only tree walk. A null bookmark passed to GetFirstChild means "the root", i.e. the
    // first top-level entry. Titles come back as UTF-16LE bytes including the terminator.

    public const uint PDFACTION_GOTO = 1;

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern IntPtr FPDFBookmark_GetFirstChild(IntPtr document, IntPtr bookmark);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern IntPtr FPDFBookmark_GetNextSibling(IntPtr document, IntPtr bookmark);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern uint FPDFBookmark_GetTitle(IntPtr bookmark, [Out] byte[]? buffer, uint bufferLength);

    /// <summary>Number of descendants if the entry is open by default, the negated number if it is closed, 0 if it has none.</summary>
    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern int FPDFBookmark_GetCount(IntPtr bookmark);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern IntPtr FPDFBookmark_GetDest(IntPtr document, IntPtr bookmark);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern IntPtr FPDFBookmark_GetAction(IntPtr bookmark);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern uint FPDFAction_GetType(IntPtr action);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern IntPtr FPDFAction_GetDest(IntPtr document, IntPtr action);

    /// <summary>0-based page index a destination points at, or -1 if it cannot be resolved.</summary>
    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern int FPDFDest_GetDestPageIndex(IntPtr document, IntPtr dest);

    // ---- fpdf_signature.h : digital signatures (listing only - PDFium never verifies them) ---
    // Experimental API in the headers, present in the shipped library. Contents is the DER
    // signature value, ByteRange int pairs (offset, length), SubFilter/Time 7-bit ASCII with a
    // trailing NUL, Reason UTF-16LE with a trailing NUL. Every getter returns the size it needs
    // when handed a null buffer.

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern int FPDF_GetSignatureCount(IntPtr document);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern IntPtr FPDF_GetSignatureObject(IntPtr document, int index);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern uint FPDFSignatureObj_GetContents(IntPtr signature, [Out] byte[]? buffer, uint length);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern uint FPDFSignatureObj_GetByteRange(IntPtr signature, [Out] int[]? buffer, uint length);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern uint FPDFSignatureObj_GetSubFilter(IntPtr signature, [Out] byte[]? buffer, uint length);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern uint FPDFSignatureObj_GetReason(IntPtr signature, [Out] byte[]? buffer, uint length);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern uint FPDFSignatureObj_GetTime(IntPtr signature, [Out] byte[]? buffer, uint length);

    // ---- fpdf_annot.h / fpdf_edit.h : creating and editing annotations (note, stamp) ----------
    // Signatures checked against the vendored headers (bblanchon.PDFium.Win32 153.0.7999).
    // FPDF_BYTESTRING keys are 8-bit (LPStr); FPDF_WIDESTRING values are UTF-16LE with a
    // terminator (LPWStr). A stamp annotation's look is not drawn by PDFium itself: it is the
    // page objects appended to it with FPDFAnnot_AppendObject (only ink and stamp support that).

    [StructLayout(LayoutKind.Sequential)]
    public struct FS_MATRIX
    {
        public float A;
        public float B;
        public float C;
        public float D;
        public float E;
        public float F;
    }

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern bool FPDFPage_RemoveAnnot(IntPtr page, int index);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern int FPDFPage_GetAnnotIndex(IntPtr page, IntPtr annot);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern bool FPDFAnnot_SetFlags(IntPtr annot, int flags);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern int FPDFAnnot_GetFlags(IntPtr annot);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern bool FPDFAnnot_SetStringValue(
        IntPtr annot, [MarshalAs(UnmanagedType.LPStr)] string key, [MarshalAs(UnmanagedType.LPWStr)] string value);

    /// <summary>Returns the byte length including the UTF-16 terminator; <paramref name="buffer"/> null just asks for the size.</summary>
    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern uint FPDFAnnot_GetStringValue(
        IntPtr annot, [MarshalAs(UnmanagedType.LPStr)] string key, [Out] byte[]? buffer, uint bufferLength);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern bool FPDFAnnot_AppendObject(IntPtr annot, IntPtr pageObject);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern int FPDFAnnot_GetObjectCount(IntPtr annot);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern bool FPDFAnnot_SetBorder(IntPtr annot, float horizontalRadius, float verticalRadius, float borderWidth);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern IntPtr FPDFPageObj_NewTextObj(
        IntPtr document, [MarshalAs(UnmanagedType.LPStr)] string font, float fontSize);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern bool FPDFText_SetText(IntPtr textObject, [MarshalAs(UnmanagedType.LPWStr)] string text);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern IntPtr FPDFPageObj_CreateNewRect(float x, float y, float w, float h);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern bool FPDFPath_SetDrawMode(IntPtr path, int fillMode, bool stroke);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern bool FPDFPageObj_SetStrokeColor(IntPtr pageObject, uint r, uint g, uint b, uint a);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern bool FPDFPageObj_SetFillColor(IntPtr pageObject, uint r, uint g, uint b, uint a);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern bool FPDFPageObj_SetStrokeWidth(IntPtr pageObject, float width);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern bool FPDFPageObj_GetBounds(IntPtr pageObject, out float left, out float bottom, out float right, out float top);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern void FPDFPageObj_Transform(IntPtr pageObject, double a, double b, double c, double d, double e, double f);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern bool FPDFPageObj_SetMatrix(IntPtr pageObject, ref FS_MATRIX matrix);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern void FPDFPageObj_Destroy(IntPtr pageObject);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern IntPtr FPDFPageObj_NewImageObj(IntPtr document);

    /// <summary>The bitmap is copied into the image object; <paramref name="pages"/> may be null/0 for an object that is not on a loaded page yet.</summary>
    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern bool FPDFImageObj_SetBitmap(IntPtr pages, int count, IntPtr imageObject, IntPtr bitmap);

    // Existing page content, read-only here: the feasibility probe for editing text in place.
    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern int FPDFPage_CountObjects(IntPtr page);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern IntPtr FPDFPage_GetObject(IntPtr page, int index);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern int FPDFPageObj_GetType(IntPtr pageObject);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern uint FPDFTextObj_GetText(IntPtr textObject, IntPtr textPage, [Out] byte[]? buffer, uint length);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern bool FPDFPage_GenerateContent(IntPtr page);

    // ---- Editing existing text objects, and embedding a font for them (M3) ------------------

    public const int FPDF_FONT_TYPE1 = 1;
    public const int FPDF_FONT_TRUETYPE = 2;

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern IntPtr FPDFTextObj_GetFont(IntPtr textObject);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern bool FPDFTextObj_GetFontSize(IntPtr textObject, out float size);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern int FPDFTextObj_GetTextRenderMode(IntPtr textObject);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern bool FPDFTextObj_SetTextRenderMode(IntPtr textObject, int renderMode);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern UIntPtr FPDFFont_GetBaseFontName(IntPtr font, [Out] byte[]? buffer, UIntPtr length);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern UIntPtr FPDFFont_GetFamilyName(IntPtr font, [Out] byte[]? buffer, UIntPtr length);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern int FPDFFont_GetIsEmbedded(IntPtr font);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern int FPDFFont_GetFlags(IntPtr font);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern int FPDFFont_GetWeight(IntPtr font);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern bool FPDFFont_GetItalicAngle(IntPtr font, out int angle);

    /// <summary>The font's decoded file data when it is embedded (otherwise the substitute font's); <paramref name="outLength"/> is the size needed.</summary>
    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern bool FPDFFont_GetFontData(IntPtr font, [Out] byte[]? buffer, UIntPtr bufferLength, out UIntPtr outLength);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern bool FPDFFont_GetGlyphWidth(IntPtr font, uint glyph, float fontSize, out float width);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern IntPtr FPDFFont_GetGlyphPath(IntPtr font, uint glyph, float fontSize);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern int FPDFGlyphPath_CountGlyphSegments(IntPtr glyphPath);

    /// <summary>Loads (and embeds, whole) a TrueType/Type1 font into the document. <paramref name="data"/> is copied.</summary>
    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern IntPtr FPDFText_LoadFont(IntPtr document, byte[] data, uint size, int fontType, bool cid);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern void FPDFFont_Close(IntPtr font);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern IntPtr FPDFPageObj_CreateTextObj(IntPtr document, IntPtr font, float fontSize);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern bool FPDFPageObj_GetMatrix(IntPtr pageObject, out FS_MATRIX matrix);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern bool FPDFPageObj_GetFillColor(IntPtr pageObject, out uint r, out uint g, out uint b, out uint a);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern bool FPDFPage_InsertObjectAtIndex(IntPtr page, IntPtr pageObject, UIntPtr index);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern bool FPDFPage_RemoveObject(IntPtr page, IntPtr pageObject);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern IntPtr FPDFTextObj_GetRenderedBitmap(IntPtr document, IntPtr page, IntPtr textObject, float scale);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern IntPtr FPDFBitmap_GetBuffer(IntPtr bitmap);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern int FPDFBitmap_GetWidth(IntPtr bitmap);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern int FPDFBitmap_GetHeight(IntPtr bitmap);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern int FPDFBitmap_GetStride(IntPtr bitmap);
}
