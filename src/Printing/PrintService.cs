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
    public bool TryShowPrintDialogAndPrint(IntPtr ownerHwnd, PdfDocument document, string documentTitle)
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

        printDoc.PrintPage += (_, e) => PrintPage(e, document, ref nextPageIndex, lastPage - 1);
        printDoc.Print();
        return true;
    }

    private static void PrintPage(PrintPageEventArgs e, PdfDocument document, ref int nextPageIndex, int lastPageIndexInclusive)
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

        using var page = document.GetPage(nextPageIndex);
        var bgra = page.RenderToBgra(pixelWidth, pixelHeight);

        var handle = GCHandle.Alloc(bgra, GCHandleType.Pinned);
        try
        {
            var stride = pixelWidth * 4;
            using var bitmap = new Bitmap(pixelWidth, pixelHeight, stride, PixelFormat.Format32bppRgb, handle.AddrOfPinnedObject());
            e.Graphics.DrawImage(bitmap, e.PageBounds);
        }
        finally
        {
            handle.Free();
        }

        nextPageIndex++;
        e.HasMorePages = nextPageIndex <= lastPageIndexInclusive;
    }

    private sealed class Win32Window(IntPtr handle) : IWin32Window
    {
        public IntPtr Handle { get; } = handle;
    }
}
