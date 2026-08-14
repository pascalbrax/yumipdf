using PdfEngine.Native;

namespace PdfEngine;

/// <summary>
/// Process-wide PDFium library lifecycle, and the single lock every PdfEngine type uses
/// to serialize calls into pdfium.dll. PDFium (built without its optional multi-threading
/// support, which this project does not enable) is not safe to call into from more than one
/// thread at a time - not even for unrelated documents/pages - so <see cref="PdfDocument"/>
/// and <see cref="PdfPage"/> take <see cref="Gate"/> around every native call rather than
/// leaving that up to callers.
/// </summary>
public static class PdfiumRuntime
{
    /// <summary>
    /// Held around every call into pdfium.dll. <c>lock</c>/<see cref="System.Threading.Monitor"/>
    /// is re-entrant on the same thread, so nested locking calls (e.g. <see cref="PdfDocument.GetPage"/>
    /// called from within a method that already holds the lock) are safe.
    /// </summary>
    internal static readonly object Gate = new();

    private static bool _initialized;

    public static void EnsureInitialized()
    {
        if (_initialized)
        {
            return;
        }

        lock (Gate)
        {
            if (_initialized)
            {
                return;
            }

            PdfiumNative.FPDF_InitLibrary();
            _initialized = true;
        }
    }
}
