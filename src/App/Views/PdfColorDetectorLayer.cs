using System.ComponentModel;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using PdfEngine.Viewing;
using Pdf26.ViewModels;
using Windows.Foundation;
using Windows.UI;

namespace Pdf26.Views;

/// <summary>
/// The colour detector's layer on one page (see MainWindow.xaml's page template). While the detector is on it covers the page, so the
/// pointer is a cross whatever is underneath and a click does nothing else; when the pointer stays still for a second it reads the
/// pixel of the page's picture under it and shows a popup below the pointer: the RGB value, the hex code and the colour's name
/// (<see cref="ColorNamer"/>: the name, or the nearest one). Off, the layer is not hit-test visible and costs nothing.
/// </summary>
/// <remarks>
/// The colour is that of the picture on screen (what <c>PdfPageViewModel.Bitmap</c> holds at the zoom the page has), so the edge of a
/// letter is a blend of the letter's colour and the paper's, and a page still being redrawn after a zoom is a stretched picture:
/// to read a flat colour, point at the middle of a filled area.
/// </remarks>
public sealed class PdfColorDetectorLayer : Canvas
{
    public static readonly DependencyProperty PageProperty = DependencyProperty.Register(
        nameof(Page), typeof(PdfPageViewModel), typeof(PdfColorDetectorLayer), new PropertyMetadata(null, OnPageChanged));

    /// <summary>How long the pointer has to stay still before the popup shows.</summary>
    private static readonly TimeSpan Dwell = TimeSpan.FromMilliseconds(1000);

    /// <summary>A hand is never perfectly still: movement under this many pixels is still "staying still".</summary>
    private const double Tolerance = 3;

    private DispatcherQueueTimer? _timer;
    private Popup? _popup;
    private Point _anchor;
    private Point _anchorInWindow;
    private bool _hasAnchor;

    public PdfColorDetectorLayer()
    {
        // A transparent brush is what makes a Canvas hit-test visible everywhere; the layer only is while the detector is on.
        Background = new SolidColorBrush(Colors.Transparent);
        IsHitTestVisible = false;
        PointerMoved += OnPointerMoved;
        PointerExited += (_, _) => Cancel();
        PointerWheelChanged += (_, _) => Cancel();
        Unloaded += (_, _) => Cancel();
    }

    public PdfPageViewModel? Page
    {
        get => (PdfPageViewModel?)GetValue(PageProperty);
        set => SetValue(PageProperty, value);
    }

    private static void OnPageChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var self = (PdfColorDetectorLayer)d;
        if (e.OldValue is PdfPageViewModel oldPage)
        {
            oldPage.Owner.PropertyChanged -= self.OnOwnerPropertyChanged;
        }

        if (e.NewValue is PdfPageViewModel newPage)
        {
            newPage.Owner.PropertyChanged += self.OnOwnerPropertyChanged;
        }

        self.Apply();
    }

    private void OnOwnerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsColorDetectorActive))
        {
            Apply();
        }
    }

    /// <summary>On: the layer takes the pointer and shows the cross. Off: it lets everything through and the popup goes away.</summary>
    private void Apply()
    {
        var on = Page?.Owner.IsColorDetectorActive == true;
        IsHitTestVisible = on;
        ProtectedCursor = on ? InputSystemCursor.Create(InputSystemCursorShape.Cross) : null;
        if (!on)
        {
            Cancel();
        }
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var position = e.GetCurrentPoint(this).Position;
        if (_hasAnchor && _timer is { IsRunning: true } && Distance(position, _anchor) <= Tolerance)
        {
            return; // still the same wait
        }

        if (_hasAnchor && _popup is { IsOpen: true } && Distance(position, _anchor) <= Tolerance)
        {
            return; // the popup is showing for this spot
        }

        Cancel();
        _anchor = position;
        _anchorInWindow = e.GetCurrentPoint(null).Position;
        _hasAnchor = true;

        _timer ??= CreateTimer();
        _timer.Start();
    }

    private DispatcherQueueTimer CreateTimer()
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = Dwell;
        timer.IsRepeating = false;
        timer.Tick += (_, _) => Show();
        return timer;
    }

    private static double Distance(Point a, Point b) => Math.Max(Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));

    /// <summary>Stops waiting and hides the popup.</summary>
    private void Cancel()
    {
        _timer?.Stop();
        _hasAnchor = false;
        if (_popup is { IsOpen: true })
        {
            _popup.IsOpen = false;
        }
    }

    private void Show()
    {
        if (Page is not { } page || !page.Owner.IsColorDetectorActive || !_hasAnchor || XamlRoot is null)
        {
            return;
        }

        if (ReadPixel(page, _anchor) is not { } rgb)
        {
            return;
        }

        var name = ColorNamer.Describe(rgb.R, rgb.G, rgb.B);
        var content = BuildContent(rgb, name);
        content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = content.DesiredSize;

        // Below and a little right of the pointer; above or left of it when there is no room.
        var window = XamlRoot.Size;
        var x = _anchorInWindow.X + 14;
        var y = _anchorInWindow.Y + 24;
        if (x + size.Width > window.Width)
        {
            x = Math.Max(0, _anchorInWindow.X - size.Width - 8);
        }

        if (y + size.Height > window.Height)
        {
            y = Math.Max(0, _anchorInWindow.Y - size.Height - 12);
        }

        _popup ??= new Popup { IsLightDismissEnabled = false };
        _popup.XamlRoot = XamlRoot;
        _popup.Child = content;
        _popup.HorizontalOffset = x;
        _popup.VerticalOffset = y;
        _popup.IsOpen = true;
    }

    /// <summary>The colour of the page's picture at <paramref name="position"/> (pixels of the page as shown), or null with no picture there.</summary>
    private static (byte R, byte G, byte B)? ReadPixel(PdfPageViewModel page, Point position)
    {
        if (page.Bitmap is not { } bitmap || page.PixelWidth <= 0 || page.PixelHeight <= 0)
        {
            return null;
        }

        // The picture may not be at the page's current size (a zoom stretches it until the page is drawn again).
        var x = (int)Math.Floor(position.X / page.PixelWidth * bitmap.PixelWidth);
        var y = (int)Math.Floor(position.Y / page.PixelHeight * bitmap.PixelHeight);
        if (x < 0 || y < 0 || x >= bitmap.PixelWidth || y >= bitmap.PixelHeight)
        {
            return null;
        }

        var pixel = new byte[4];
        bitmap.PixelBuffer.CopyTo((uint)((y * bitmap.PixelWidth + x) * 4), pixel, 0, 4); // B, G, R, x
        return (pixel[2], pixel[1], pixel[0]);
    }

    private static FrameworkElement BuildContent((byte R, byte G, byte B) rgb, ColorName name)
    {
        var swatch = new Border
        {
            Width = 44,
            Height = 44,
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Color.FromArgb(255, rgb.R, rgb.G, rgb.B)),
            BorderBrush = (Brush)Application.Current.Resources["ControlStrongStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
        };

        var text = new StackPanel { Spacing = 2 };
        text.Children.Add(new TextBlock
        {
            Text = name.IsExact ? name.Name : $"{name.Name} (più vicino)",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            FontSize = 15,
        });
        text.Children.Add(new TextBlock { Text = $"RGB  {rgb.R}, {rgb.G}, {rgb.B}" });
        text.Children.Add(new TextBlock { Text = $"#{rgb.R:X2}{rgb.G:X2}{rgb.B:X2}", Opacity = 0.7 });

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        row.Children.Add(swatch);
        row.Children.Add(text);

        return new Border
        {
            Child = row,
            Padding = new Thickness(12, 10, 14, 10),
            CornerRadius = new CornerRadius(8),
            Background = (Brush)Application.Current.Resources["SolidBackgroundFillColorBaseBrush"],
            BorderBrush = (Brush)Application.Current.Resources["SurfaceStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(1),
            IsHitTestVisible = false,
        };
    }
}
