using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
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

    /// <summary>
    /// Where the current search query was found on this page (see <c>MainViewModel</c>'s
    /// "Text search / OCR" region, the only place that calls <see cref="SetHighlights"/>) -
    /// zero or more highlight rectangles, drawn on top of <see cref="Bitmap"/> the same way a
    /// form field's overlay is. Empty for every page except the one the user just searched to.
    /// </summary>
    public ObservableCollection<PdfSearchHighlightViewModel> Highlights { get; } = new();

    /// <summary>Rects in PDF point space (left, bottom, right, top) - kept so <see cref="Resize"/> can rebuild <see cref="Highlights"/> at a new zoom without needing them recomputed.</summary>
    private IReadOnlyList<(double Left, double Bottom, double Right, double Top)> _highlightRectsPt = Array.Empty<(double, double, double, double)>();

    /// <summary>Replaces every highlight on this page - an empty list clears them (called when the page is no longer the current search match, or the query changes/closes).</summary>
    public void SetHighlights(IReadOnlyList<(double Left, double Bottom, double Right, double Top)> rectsPt, double zoom)
    {
        _highlightRectsPt = rectsPt;
        RebuildHighlights(zoom);
    }

    private void RebuildHighlights(double zoom)
    {
        Highlights.Clear();
        foreach (var (left, bottom, right, top) in _highlightRectsPt)
        {
            var margin = new Thickness(left * zoom, (PointHeight - top) * zoom, 0, 0);
            var width = Math.Max(1, (right - left) * zoom);
            var height = Math.Max(1, (top - bottom) * zoom);
            Highlights.Add(new PdfSearchHighlightViewModel(margin, width, height));
        }
    }

    /// <summary>Rescales the page's on-screen size (and every field/highlight overlay's position) to a new zoom level. Does not touch <see cref="Bitmap"/>.</summary>
    public void Resize(double zoom)
    {
        PixelWidth = ToPixels(PointWidth, zoom);
        PixelHeight = ToPixels(PointHeight, zoom);

        foreach (var field in Fields)
        {
            field.UpdateOverlay(zoom, PointHeight);
        }

        if (_highlightRectsPt.Count > 0)
        {
            RebuildHighlights(zoom);
        }
    }

    private static double ToPixels(double points, double zoom) => Math.Max(1, points * zoom);
}
