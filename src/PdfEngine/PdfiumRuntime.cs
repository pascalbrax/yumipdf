using PdfEngine.Native;

namespace PdfEngine;

/// <summary>
/// Process-wide PDFium library lifecycle. Call <see cref="EnsureInitialized"/> once before
/// touching any other PdfEngine type; PDFium is not safe to use concurrently from multiple
/// threads, so callers should serialize access to documents (e.g. via a single dispatcher
/// thread) rather than relying on this class for thread-safety.
/// </summary>
public static class PdfiumRuntime
{
    private static bool _initialized;
    private static readonly object Gate = new();

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
