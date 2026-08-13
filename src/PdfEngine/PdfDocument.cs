using System.Runtime.InteropServices;
using PdfEngine.Native;

namespace PdfEngine;

/// <summary>
/// A loaded PDF document. PDFium keeps reading from the source buffer for the lifetime of
/// the document, so <see cref="PdfDocument"/> pins the bytes it was opened from until
/// <see cref="Dispose"/> - always dispose documents (or wrap in <c>using</c>) rather than
/// relying on the GC.
/// </summary>
public sealed class PdfDocument : IDisposable
{
    private readonly IntPtr _handle;
    private readonly GCHandle? _pinnedData;
    private bool _disposed;

    private PdfDocument(IntPtr handle, GCHandle? pinnedData)
    {
        _handle = handle;
        _pinnedData = pinnedData;
    }

    /// <summary>Creates a new, empty in-memory document (used as the target of merge/split operations).</summary>
    internal static PdfDocument CreateEmpty()
    {
        PdfiumRuntime.EnsureInitialized();
        var handle = PdfiumNative.FPDF_CreateNewDocument();
        if (handle == IntPtr.Zero)
        {
            throw PdfException.FromLastPdfiumError("Impossibile creare un nuovo documento PDF.");
        }

        return new PdfDocument(handle, pinnedData: null);
    }

    /// <summary>
    /// Opens a PDF already fully read into memory. Use this for content fetched over
    /// HTTP(S) or staged from a UNC share by the app's file-source resolution layer.
    /// </summary>
    public static PdfDocument OpenFromBytes(byte[] data, string? password = null)
    {
        PdfiumRuntime.EnsureInitialized();
        ArgumentNullException.ThrowIfNull(data);

        var pinned = GCHandle.Alloc(data, GCHandleType.Pinned);
        var handle = PdfiumNative.FPDF_LoadMemDocument64(data, data.LongLength, password);
        if (handle == IntPtr.Zero)
        {
            pinned.Free();
            throw PdfException.FromLastPdfiumError("Impossibile aprire il documento PDF.");
        }

        return new PdfDocument(handle, pinned);
    }

    /// <summary>
    /// Opens a PDF from a local or UNC filesystem path (e.g. <c>C:\docs\file.pdf</c> or
    /// <c>\\server\share\file.pdf</c>). <see cref="File.ReadAllBytes(string)"/> handles both
    /// transparently through the Win32 path APIs.
    /// </summary>
    public static PdfDocument OpenFromPath(string path, string? password = null)
        => OpenFromBytes(File.ReadAllBytes(path), password);

    public int PageCount
    {
        get
        {
            ThrowIfDisposed();
            return PdfiumNative.FPDF_GetPageCount(_handle);
        }
    }

    public PdfPage GetPage(int index)
    {
        ThrowIfDisposed();
        if (index < 0 || index >= PageCount)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        var pageHandle = PdfiumNative.FPDF_LoadPage(_handle, index);
        if (pageHandle == IntPtr.Zero)
        {
            throw PdfException.FromLastPdfiumError($"Impossibile caricare la pagina {index}.");
        }

        return new PdfPage(pageHandle, index);
    }

    /// <summary>Writes the (possibly modified) document to <paramref name="destinationPath"/>.</summary>
    public void SaveAs(string destinationPath, bool removeSecurity = false)
    {
        ThrowIfDisposed();
        using var stream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write);

        PdfiumNative.WriteBlockDelegate writeBlock = (_, data, size) =>
        {
            var chunk = new byte[size];
            Marshal.Copy(data, chunk, 0, (int)size);
            stream.Write(chunk, 0, (int)size);
            return 1;
        };

        var fileWrite = new PdfiumNative.FPDF_FILEWRITE
        {
            version = 1,
            WriteBlock = writeBlock,
        };

        var flags = removeSecurity ? PdfiumNative.FPDF_REMOVE_SECURITY : PdfiumNative.FPDF_NO_INCREMENTAL;
        var ok = PdfiumNative.FPDF_SaveAsCopy(_handle, ref fileWrite, flags);
        GC.KeepAlive(writeBlock);
        if (ok == 0)
        {
            throw PdfException.FromLastPdfiumError($"Impossibile salvare il documento in '{destinationPath}'.");
        }
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
            throw new ObjectDisposedException(nameof(PdfDocument));
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        PdfiumNative.FPDF_CloseDocument(_handle);
        if (_pinnedData is { IsAllocated: true } pinned)
        {
            pinned.Free();
        }

        _disposed = true;
    }
}
