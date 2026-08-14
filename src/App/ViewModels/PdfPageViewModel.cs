using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Pdf26.ViewModels;

/// <summary>
/// A single page in the continuous-scroll page list. Created immediately with its final
/// pixel size (known from <c>FPDF_GetPageWidth</c>/<c>Height</c>, which does not require
/// rendering) so the page list lays out at the right scroll height right away; <see cref="Bitmap"/>
/// is filled in afterwards, in the background, without shifting the layout.
/// </summary>
public partial class PdfPageViewModel : ObservableObject
{
    public PdfPageViewModel(int index, double pixelWidth, double pixelHeight)
    {
        Index = index;
        PixelWidth = pixelWidth;
        PixelHeight = pixelHeight;
    }

    public int Index { get; }

    public double PixelWidth { get; }

    public double PixelHeight { get; }

    [ObservableProperty]
    public partial WriteableBitmap? Bitmap { get; set; }
}
