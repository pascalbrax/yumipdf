using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Printing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using PdfEngine;

namespace Printing;

/// <summary>
/// Printing via classic GDI (<see cref="System.Drawing.Printing"/>), fed by page bitmaps
/// rendered through <see cref="PdfEngine"/>. Lives in its own project (not the WinUI3 App
/// project) because mixing UseWinUI and UseWindowsForms in one csproj breaks the WinUI3
/// XAML compiler - see the comment in Printing.csproj.
/// </summary>
public sealed class PrintService
{
    /// <summary>Shows the native print dialog and, if confirmed, prints the requested pages.</summary>
    /// <param name="ownerHwnd">The main window's HWND (from <c>WinRT.Interop.WindowNative.GetWindowHandle</c>), used to own the print dialog.</param>
    /// <param name="renderPage">
    /// Renders a 0-based page index at a pixel size to a top-down BGRA buffer. Pass the same
    /// renderer the on-screen view uses (the app routes it through
    /// <c>PdfFormFiller.RenderPageToBgra</c> while a form filler is open): a value filled in
    /// but not yet flattened lives only in PDFium's live form-fill state, and plain
    /// <see cref="PdfPage.RenderToBgra"/> paints the widgets' last-generated appearance
    /// stream, so printing through it produced sheets without the user's entries. When
    /// <see langword="null"/>, falls back to <see cref="PdfPage.RenderToBgra"/>.
    /// </param>
    public bool TryShowPrintDialogAndPrint(
        IntPtr ownerHwnd, PdfDocument document, string documentTitle, Func<int, int, int, byte[]>? renderPage = null)
    {
        ArgumentNullException.ThrowIfNull(document);

        using var printDialog = new PrintDialog
        {
            AllowSomePages = true,
            AllowSelection = false,
            UseEXDialog = true,
        };
        printDialog.PrinterSettings.MinimumPage = 1;
        printDialog.PrinterSettings.MaximumPage = document.PageCount;
        printDialog.PrinterSettings.FromPage = 1;
        printDialog.PrinterSettings.ToPage = document.PageCount;

        if (printDialog.ShowDialog(new Win32Window(ownerHwnd)) != DialogResult.OK)
        {
            return false;
        }

        using var printDoc = new System.Drawing.Printing.PrintDocument
        {
            DocumentName = documentTitle,
            PrinterSettings = printDialog.PrinterSettings,
        };

        var (firstPage, lastPage) = printDialog.PrinterSettings.PrintRange switch
        {
            PrintRange.SomePages => (printDialog.PrinterSettings.FromPage, printDialog.PrinterSettings.ToPage),
            _ => (1, document.PageCount),
        };
        var nextPageIndex = firstPage - 1; // convert to 0-based

        // Every page's pinned pixel buffer is kept alive (not freed page-by-page) until the
        // whole job is done - see PrintPage's remarks for why.
        var pinnedPageBuffers = new List<GCHandle>();
        try
        {
            printDoc.PrintPage += (_, e) => PrintPage(e, document, renderPage, ref nextPageIndex, lastPage - 1, pinnedPageBuffers);
            printDoc.Print();
        }
        finally
        {
            foreach (var handle in pinnedPageBuffers)
            {
                handle.Free();
            }
        }

        return true;
    }

    /// <remarks>
    /// A page's pinned BGRA buffer used to be freed (<c>GCHandle.Free</c>) right after the one
    /// <c>Graphics.DrawImage</c> call that used it, inside this same method - the same pattern
    /// that, for a PDFium-owned pointer, turned out to cause a real crash (see
    /// <c>PdfEngine.Forms.PdfFormFiller</c>'s remarks on <c>_formInfoPtr</c>): freeing/unpinning
    /// memory immediately after the one call that appears to consume it is only safe if that
    /// callee is guaranteed to have fully finished with the pointer by the time the call
    /// returns. Many printer drivers spool through EMF and don't actually rasterize a
    /// <c>DrawImage</c> call's source bitmap until spooling happens, well after
    /// <c>PrintPage</c> has returned - freeing the buffer that early crashed the app right
    /// after a print job actually completed (the printed output looked fine; the buffer was
    /// still valid long enough for whatever *did* run synchronously). Keeping every page's
    /// buffer pinned for the whole job removes that same class of bug here too.
    /// </remarks>
    private static void PrintPage(PrintPageEventArgs e, PdfDocument document, Func<int, int, int, byte[]>? renderPage, ref int nextPageIndex, int lastPageIndexInclusive, List<GCHandle> pinnedPageBuffers)
    {
        if (e.Graphics is null || e.PageBounds.Width <= 0 || e.PageBounds.Height <= 0)
        {
            e.HasMorePages = false;
            return;
        }

        var dpiX = e.Graphics.DpiX;
        var dpiY = e.Graphics.DpiY;
        var pixelWidth = (int)Math.Round(e.PageBounds.Width * dpiX / 100.0);
        var pixelHeight = (int)Math.Round(e.PageBounds.Height * dpiY / 100.0);

        byte[] bgra;
        if (renderPage is not null)
        {
            bgra = renderPage(nextPageIndex, pixelWidth, pixelHeight);
        }
        else
        {
            using var page = document.GetPage(nextPageIndex);
            bgra = page.RenderToBgra(pixelWidth, pixelHeight);
        }

        var handle = GCHandle.Alloc(bgra, GCHandleType.Pinned);
        pinnedPageBuffers.Add(handle);

        var stride = pixelWidth * 4;
        using var bitmap = new Bitmap(pixelWidth, pixelHeight, stride, PixelFormat.Format32bppRgb, handle.AddrOfPinnedObject());
        e.Graphics.DrawImage(bitmap, e.PageBounds);

        nextPageIndex++;
        e.HasMorePages = nextPageIndex <= lastPageIndexInclusive;
    }

    private sealed class Win32Window(IntPtr handle) : IWin32Window
    {
        public IntPtr Handle { get; } = handle;
    }
}
