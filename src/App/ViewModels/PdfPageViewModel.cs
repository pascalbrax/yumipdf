using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Pdf26.ViewModels;

/// <summary>
/// A single page in the continuous-scroll page list. <see cref="PixelWidth"/>/<see cref="PixelHeight"/>
/// change immediately on a zoom change (see <see cref="Resize"/>), before the page is
/// re-rendered at the new resolution, so the on-screen page resizes instantly with whatever
/// bitmap it already has stretched (or shrunk) to fit - a fast, pixelated "fake zoom" - while
/// <see cref="Bitmap"/> is replaced with a crisp one in the background.
/// </summary>
public partial class PdfPageViewModel : ObservableObject
{
    public PdfPageViewModel(int index, double pointWidth, double pointHeight, double zoom)
    {
        Index = index;
        PointWidth = pointWidth;
        PointHeight = pointHeight;
        PixelWidth = ToPixels(pointWidth, zoom);
        PixelHeight = ToPixels(pointHeight, zoom);
    }

    public int Index { get; }

    /// <summary>Page size in PDF points (zoom-independent), kept so <see cref="Resize"/> never needs to touch PDFium.</summary>
    public double PointWidth { get; }

    public double PointHeight { get; }

    [ObservableProperty]
    public partial double PixelWidth { get; set; }

    [ObservableProperty]
    public partial double PixelHeight { get; set; }

    [ObservableProperty]
    public partial WriteableBitmap? Bitmap { get; set; }

    /// <summary>Rescales the page's on-screen size to a new zoom level. Does not touch <see cref="Bitmap"/>.</summary>
    public void Resize(double zoom)
    {
        PixelWidth = ToPixels(PointWidth, zoom);
        PixelHeight = ToPixels(PointHeight, zoom);
    }

    private static double ToPixels(double points, double zoom) => Math.Max(1, points * zoom);
}
