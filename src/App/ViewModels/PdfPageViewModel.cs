using System.Collections.ObjectModel;
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

    /// <summary>
    /// Editable/checkable AcroForm fields on this page (populated once, when the page is
    /// first created - see <c>MainViewModel.RunRenderAsync</c>), drawn as an in-place overlay
    /// on top of <see cref="Bitmap"/> so the user can type/click directly on the page instead
    /// of only through the side "Compila modulo" panel. Read-only field types are not
    /// included (nothing to overlay). Empty for documents/pages with no form.
    /// </summary>
    public ObservableCollection<FormFieldEntryViewModel> Fields { get; } = new();

    /// <summary>Rescales the page's on-screen size (and every field overlay's position) to a new zoom level. Does not touch <see cref="Bitmap"/>.</summary>
    public void Resize(double zoom)
    {
        PixelWidth = ToPixels(PointWidth, zoom);
        PixelHeight = ToPixels(PointHeight, zoom);

        foreach (var field in Fields)
        {
            field.UpdateOverlay(zoom, PointHeight);
        }
    }

    private static double ToPixels(double points, double zoom) => Math.Max(1, points * zoom);
}
