using Microsoft.UI.Xaml.Media.Imaging;

namespace Pdf26.ViewModels;

/// <summary>A single rendered page, as displayed in the continuous-scroll page list.</summary>
public sealed class PdfPageViewModel(int index, WriteableBitmap bitmap)
{
    public int Index { get; } = index;

    public WriteableBitmap Bitmap { get; } = bitmap;
}
