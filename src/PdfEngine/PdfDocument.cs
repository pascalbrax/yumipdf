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
        return PdfiumRuntime.Invoke(() =>
        {
            var handle = PdfiumNative.FPDF_CreateNewDocument();
            if (handle == IntPtr.Zero)
            {
                throw PdfException.FromLastPdfiumError("Impossibile creare un nuovo documento PDF.");
            }

            return new PdfDocument(handle, pinnedData: null);
        });
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
        return PdfiumRuntime.Invoke(() =>
        {
            var handle = PdfiumNative.FPDF_LoadMemDocument64(data, data.LongLength, password);
            if (handle == IntPtr.Zero)
            {
                pinned.Free();
                throw PdfException.FromLastPdfiumError("Impossibile aprire il documento PDF.");
            }

            return new PdfDocument(handle, pinned);
        });
    }

    /// <summary>
    /// Opens a PDF from a local or UNC filesystem path (e.g. <c>C:\docs\file.pdf</c> or
    /// <c>\\server\share\file.pdf</c>). <see cref="File.ReadAllBytes(string)"/> handles both
    /// transparently through the Win32 path APIs.
    /// </summary>
    public static PdfDocument OpenFromPath(string path, string? password = null)
        => OpenFromBytes(File.ReadAllBytes(path), password);

    public int PageCount
        => PdfiumRuntime.Invoke(() =>
        {
            ThrowIfDisposed();
            return PdfiumNative.FPDF_GetPageCount(_handle);
        });

    public PdfPage GetPage(int index)
        => PdfiumRuntime.Invoke(() =>
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

            return new PdfPage(pageHandle, index, this);
        });

    /// <summary>
    /// The document's outline as a tree, in document order - empty if it has none. Entries are
    /// read eagerly (the whole tree, not on expansion): even a large book's outline is a few
    /// thousand small strings, and a snapshot keeps the result usable after PDFium's own
    /// handles are gone.
    /// </summary>
    public IReadOnlyList<PdfBookmark> GetBookmarks()
        => PdfiumRuntime.Invoke(() =>
        {
            ThrowIfDisposed();
            var budget = MaxBookmarkEntries;
            return ReadBookmarkLevel(IntPtr.Zero, depth: 0, ref budget);
        });

    // A malformed (or hostile) file can make the outline loop back on itself or be absurdly
    // large; both limits turn that into a truncated tree instead of a hang or a stack overflow.
    private const int MaxBookmarkDepth = 64;
    private const int MaxBookmarkEntries = 100_000;

    private List<PdfBookmark> ReadBookmarkLevel(IntPtr parent, int depth, ref int budget)
    {
        var result = new List<PdfBookmark>();
        if (depth >= MaxBookmarkDepth)
        {
            return result;
        }

        var visited = new HashSet<IntPtr>();
        var node = PdfiumNative.FPDFBookmark_GetFirstChild(_handle, parent);
        while (node != IntPtr.Zero && budget > 0 && visited.Add(node))
        {
            budget--;
            var children = ReadBookmarkLevel(node, depth + 1, ref budget);
            result.Add(new PdfBookmark(
                ReadBookmarkTitle(node),
                ResolveBookmarkPage(node),
                IsInitiallyOpen: PdfiumNative.FPDFBookmark_GetCount(node) > 0,
                children));
            node = PdfiumNative.FPDFBookmark_GetNextSibling(_handle, node);
        }

        return result;
    }

    private static string ReadBookmarkTitle(IntPtr bookmark)
    {
        var length = PdfiumNative.FPDFBookmark_GetTitle(bookmark, null, 0);
        if (length <= 2)
        {
            return string.Empty;
        }

        var buffer = new byte[length];
        PdfiumNative.FPDFBookmark_GetTitle(bookmark, buffer, length);
        // The reported length includes the UTF-16 terminator.
        return System.Text.Encoding.Unicode.GetString(buffer, 0, (int)length - 2);
    }

    /// <summary>
    /// An entry leads somewhere either through a direct destination or through a GoTo action
    /// (PDFium documents that the action is only worth asking for when there is no destination).
    /// Other actions - URI, Launch, GoToR - do not point inside this document.
    /// </summary>
    private int ResolveBookmarkPage(IntPtr bookmark)
    {
        var dest = PdfiumNative.FPDFBookmark_GetDest(_handle, bookmark);
        if (dest == IntPtr.Zero)
        {
            var action = PdfiumNative.FPDFBookmark_GetAction(bookmark);
            if (action == IntPtr.Zero || PdfiumNative.FPDFAction_GetType(action) != PdfiumNative.PDFACTION_GOTO)
            {
                return -1;
            }

            dest = PdfiumNative.FPDFAction_GetDest(_handle, action);
            if (dest == IntPtr.Zero)
            {
                return -1;
            }
        }

        return PdfiumNative.FPDFDest_GetDestPageIndex(_handle, dest);
    }

    /// <summary>
    /// The document's digital signatures as it declares them, in file order - empty if it has
    /// none. Only reads them: whether they hold is <see cref="PdfSignatureVerifier"/>'s question.
    /// </summary>
    public IReadOnlyList<PdfSignature> GetSignatures()
        => PdfiumRuntime.Invoke(() =>
        {
            ThrowIfDisposed();
            var count = PdfiumNative.FPDF_GetSignatureCount(_handle);
            var result = new List<PdfSignature>();
            for (var index = 0; index < count; index++)
            {
                var signature = PdfiumNative.FPDF_GetSignatureObject(_handle, index);
                if (signature == IntPtr.Zero)
                {
                    continue;
                }

                result.Add(new PdfSignature(
                    SubFilter: ReadAscii(signature, PdfiumNative.FPDFSignatureObj_GetSubFilter),
                    Reason: ReadUtf16(signature, PdfiumNative.FPDFSignatureObj_GetReason),
                    SigningTimeText: ReadAscii(signature, PdfiumNative.FPDFSignatureObj_GetTime),
                    ByteRange: ReadByteRange(signature),
                    Contents: ReadBytes(signature, PdfiumNative.FPDFSignatureObj_GetContents)));
            }

            return result;
        });

    private delegate uint ByteGetter(IntPtr signature, byte[]? buffer, uint length);

    private static byte[] ReadBytes(IntPtr signature, ByteGetter getter)
    {
        var length = getter(signature, null, 0);
        if (length == 0)
        {
            return Array.Empty<byte>();
        }

        var buffer = new byte[length];
        getter(signature, buffer, length);
        return buffer;
    }

    /// <summary>7-bit ASCII with a trailing NUL (the length counts it) - null for an entry the signature does not have.</summary>
    private static string? ReadAscii(IntPtr signature, ByteGetter getter)
    {
        var bytes = ReadBytes(signature, getter);
        return bytes.Length > 1 ? System.Text.Encoding.ASCII.GetString(bytes, 0, bytes.Length - 1) : null;
    }

    /// <summary>UTF-16LE with a trailing NUL (the length counts both bytes of it).</summary>
    private static string? ReadUtf16(IntPtr signature, ByteGetter getter)
    {
        var bytes = ReadBytes(signature, getter);
        return bytes.Length > 2 ? System.Text.Encoding.Unicode.GetString(bytes, 0, bytes.Length - 2) : null;
    }

    private static int[] ReadByteRange(IntPtr signature)
    {
        var count = PdfiumNative.FPDFSignatureObj_GetByteRange(signature, null, 0);
        if (count == 0)
        {
            return Array.Empty<int>();
        }

        var buffer = new int[count];
        PdfiumNative.FPDFSignatureObj_GetByteRange(signature, buffer, count);
        return buffer;
    }

    /// <summary>Writes the (possibly modified) document to <paramref name="destinationPath"/>.</summary>
    public void SaveAs(string destinationPath, bool removeSecurity = false)
    {
        using var stream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write);
        WriteTo(stream, removeSecurity, $"Impossibile salvare il documento in '{destinationPath}'.");
    }

    /// <summary>The (possibly modified) document as the bytes <see cref="SaveAs"/> would write - for handing to code that edits a PDF file's structure (see <c>PdfFormDesigner</c>).</summary>
    public byte[] SaveToBytes(bool removeSecurity = false)
    {
        using var stream = new MemoryStream();
        WriteTo(stream, removeSecurity, "Impossibile serializzare il documento.");
        return stream.ToArray();
    }

    private void WriteTo(Stream stream, bool removeSecurity, string failureMessage)
        => PdfiumRuntime.Invoke(() =>
        {
            ThrowIfDisposed();

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
                throw PdfException.FromLastPdfiumError(failureMessage);
            }

            return 0;
        });

    /// <summary>Fonts and detached page objects that editing existing text keeps alive for this document - see <see cref="Editing.PdfEditingState"/>.</summary>
    internal Editing.PdfEditingState EditingState { get; } = new();

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
        => PdfiumRuntime.Invoke(() =>
        {
            if (_disposed)
            {
                return;
            }

            EditingState.DestroyAll();
            PdfiumNative.FPDF_CloseDocument(_handle);
            if (_pinnedData is { IsAllocated: true } pinned)
            {
                pinned.Free();
            }

            _disposed = true;
        });
}
