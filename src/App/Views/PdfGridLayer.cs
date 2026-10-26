using System.ComponentModel;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using ShapePath = Microsoft.UI.Xaml.Shapes.Path;
using PdfEngine.Annotations;
using Pdf26.ViewModels;
using Windows.Foundation;

namespace Pdf26.Views;

/// <summary>
/// The dotted alignment grid over one page while "Aggancia alla griglia" is on: a point every <see cref="PageGrid.StepPt"/>
/// PDF points, counted from the page's top-left corner - the same grid the app snaps to. Purely a screen overlay (never written
/// into the PDF, never hit-testable).
/// </summary>
/// <remarks>
/// One <see cref="ShapePath"/> per page whose geometry is a horizontal line per row, drawn with a dash pattern so each dash is one dot:
/// a document of a hundred pages costs a hundred elements, not half a million ellipses. Below
/// <see cref="MinGridPixels"/> px between points (zoomed far out) the dots would just be noise, so nothing is drawn - the
/// snapping is still there.
/// </remarks>
public sealed class PdfGridLayer : Canvas
{
    public static readonly DependencyProperty PageProperty = DependencyProperty.Register(
        nameof(Page), typeof(PdfPageViewModel), typeof(PdfGridLayer), new PropertyMetadata(null, OnPageChanged));

    private const double MinGridPixels = 5;
    private const double DotSize = 1.8;

    public PdfPageViewModel? Page
    {
        get => (PdfPageViewModel?)GetValue(PageProperty);
        set => SetValue(PageProperty, value);
    }

    public PdfGridLayer()
    {
        IsHitTestVisible = false;
    }

    private static void OnPageChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var self = (PdfGridLayer)d;
        if (e.OldValue is PdfPageViewModel oldPage)
        {
            oldPage.PropertyChanged -= self.OnPagePropertyChanged;
            oldPage.Owner.PropertyChanged -= self.OnOwnerPropertyChanged;
        }

        if (e.NewValue is PdfPageViewModel newPage)
        {
            newPage.PropertyChanged += self.OnPagePropertyChanged;
            newPage.Owner.PropertyChanged += self.OnOwnerPropertyChanged;
        }

        self.Rebuild();
    }

    private void OnPagePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PdfPageViewModel.PixelWidth) or nameof(PdfPageViewModel.PixelHeight))
        {
            Rebuild();
        }
    }

    private void OnOwnerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsGridActive))
        {
            Rebuild();
        }
    }

    private void Rebuild()
    {
        Children.Clear();
        if (Page is not { } page || !page.Owner.IsGridActive)
        {
            return;
        }

        var step = PageGrid.StepPt * page.Owner.Zoom;
        if (step < MinGridPixels || page.PixelWidth <= step || page.PixelHeight <= step)
        {
            return;
        }

        // One horizontal line per row, from the first column to the right edge; the dash pattern turns each line into a row of dots.
        // Dash lengths are in units of the stroke thickness: a dot is one unit long, then a gap up to the next column.
        var geometry = new GeometryGroup();
        for (var y = step; y < page.PixelHeight; y += step)
        {
            geometry.Children.Add(new LineGeometry { StartPoint = new Point(step - DotSize / 2, y), EndPoint = new Point(page.PixelWidth, y) });
        }

        Children.Add(new ShapePath
        {
            Data = geometry,
            Stroke = new SolidColorBrush(Windows.UI.Color.FromArgb(0xB0, 0x5A, 0x6B, 0x85)),
            StrokeThickness = DotSize,
            StrokeDashArray = new DoubleCollection { 1, step / DotSize - 1 },
            IsHitTestVisible = false,
        });
    }
}
