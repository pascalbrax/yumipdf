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
    public static extern IntPtr FPDFDOC_InitFormFillEnvironment(IntPtr document, ref FPDF_FORMFILLINFO formInfo);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern void FPDFDOC_ExitFormFillEnvironment(IntPtr formHandle);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern int FPDFAnnot_GetFormFieldType(IntPtr formHandle, IntPtr annot);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern uint FPDFAnnot_GetFormFieldName(
        IntPtr formHandle, IntPtr annot, byte[]? buffer, uint buflen);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern uint FPDFAnnot_GetFormFieldValue(
        IntPtr formHandle, IntPtr annot, byte[]? buffer, uint buflen);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern bool FORM_SetFocusedAnnot(IntPtr formHandle, IntPtr annot);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern bool FORM_SelectAllText(IntPtr formHandle, IntPtr page);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern void FORM_ReplaceSelection(IntPtr formHandle, IntPtr page, [MarshalAs(UnmanagedType.LPWStr)] string text);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern void FORM_OnAfterLoadPage(IntPtr page, IntPtr formHandle);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern void FORM_OnBeforeClosePage(IntPtr page, IntPtr formHandle);

    [DllImport(Lib, CallingConvention = CallingConvention.StdCall)]
    public static extern bool FORM_ForceToKillFocus(IntPtr formHandle);

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
}
