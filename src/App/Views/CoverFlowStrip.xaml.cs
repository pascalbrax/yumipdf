using System.Diagnostics;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.System;
using Pdf26.ViewModels;

namespace Pdf26.Views;

/// <summary>
/// A vertical "Cover Flow": the page at the center faces the viewer, the ones above and below
/// tilt away in perspective, shrink, overlap and fade out. The strip scrolls with the mouse
/// wheel, by dragging, with the keyboard, or by clicking a page, and every movement is
/// animated (a critically damped spring, stepped once per rendered frame).
/// </summary>
/// <remarks>
/// Only <see cref="Reach"/> pages either side of the center exist as elements at any time: a
/// fixed pool of slots that are re-pointed at whichever pages are nearest, so the cost does
/// not depend on the document's length. The pose of a slot is a pure function of how far its
/// page is from the (fractional, animated) center position, so the strip is smooth by
/// construction - a page never jumps, it just gets a slightly different distance every frame.
///
/// Hit testing is done here rather than by XAML: a tilted, shrunk element would otherwise be
/// tested against its untransformed layout box. Slots are therefore not hit-test visible, and
/// a click is mapped to a page from the same numbers that positioned the slots.
///
/// Scrolling the strip does not by itself move the document: only <see cref="PageActivated"/>
/// (a click, or Enter) does. The owner tells the strip where the document currently is
/// through <see cref="ScrollToPage"/>.
/// </remarks>
public sealed partial class CoverFlowStrip : UserControl
{
    /// <summary>How many pages are shown either side of the center one.</summary>
    private const int Reach = 7;

    private const double MaxTiltDegrees = 60;
    private const double SideScale = 0.8;
    private const double SmoothTimeSeconds = 0.17;
    private const double DragPixelsPerPage = 55;

    private sealed class Slot
    {
        public readonly Grid Root = new() { IsHitTestVisible = false, RenderTransformOrigin = new Point(0.5, 0.5) };
        public readonly Image Picture = new() { Stretch = Stretch.Fill };
        public readonly TextBlock Number = new()
        {
            FontSize = 30,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0x66, 0, 0, 0)),
        };
        public readonly Rectangle Dim = new() { Fill = new SolidColorBrush(Colors.Black), Opacity = 0, IsHitTestVisible = false };
        public readonly CompositeTransform Move = new();
        public readonly PlaneProjection Tilt = new();
        public int Index = -1;
        public double Width;
        public double Height;

        public Slot()
        {
            // Two soft, offset dark shapes stand in for a drop shadow (ThemeShadow does not
            // survive a plane projection well).
            Root.Children.Add(new Rectangle
            {
                Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(0x1C, 0, 0, 0)),
                RadiusX = 5, RadiusY = 5,
                Margin = new Thickness(-4, 6, -4, -9),
            });
            Root.Children.Add(new Rectangle
            {
                Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(0x38, 0, 0, 0)),
                RadiusX = 3, RadiusY = 3,
                Margin = new Thickness(-1, 2, -1, -3),
            });
            Root.Children.Add(new Border
            {
                Background = new SolidColorBrush(Colors.White),
                BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0x40, 0, 0, 0)),
                BorderThickness = new Thickness(1),
                Child = new Grid { Children = { Picture, Number } },
            });
            Root.Children.Add(Dim);
            Root.RenderTransform = Move;
            Root.Projection = Tilt;
        }
    }

    private readonly Slot[] _slots = new Slot[2 * Reach + 1];
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private PdfThumbnailSet? _source;

    private double _position;
    private double _velocity;
    private double _target;
    private bool _rendering;
    private double _lastFrameSeconds;
    private int _baseIndex = int.MinValue;
    private int _reportedCenter = -1;

    private double _thumbWidth = 160;
    private double _thumbBoxHeight = 226;
    private double _gapNear;
    private double _gapFar;

    private bool _pressed;
    private bool _dragging;
    private Point _pressPoint;
    private double _dragStartTarget;
    private double _wheelAccumulator;

    /// <summary>The user chose a page (clicked it, or pressed Enter on the centered one). The argument is its 0-based index.</summary>
    public event EventHandler<int>? PageActivated;

    /// <summary>The page nearest the center changed (while scrolling the strip). Used to decide which thumbnails to generate first.</summary>
    public event EventHandler<int>? CenterPageChanged;

    public CoverFlowStrip()
    {
        InitializeComponent();

        for (var k = 0; k < _slots.Length; k++)
        {
            _slots[k] = new Slot();
            ItemsCanvas.Children.Add(_slots[k].Root);
        }

        Root.PointerPressed += OnPointerPressed;
        Root.PointerMoved += OnPointerMoved;
        Root.PointerReleased += OnPointerReleased;
        Root.PointerCanceled += OnPointerCanceled;
        Root.PointerCaptureLost += OnPointerCanceled;
        Root.PointerWheelChanged += OnPointerWheelChanged;
        KeyDown += OnKeyDown;
        Unloaded += (_, _) => StopRendering();

        RecomputeLayout();
    }

    /// <summary>Number of pages in the shown document (0 when none).</summary>
    internal int PageCount => _source?.Count ?? 0;

    /// <summary>0-based index of the page nearest the center right now.</summary>
    internal int CenterPage => ClampIndex((int)Math.Round(_target));

    /// <summary>What Enter does: choose the page that is currently in the center.</summary>
    internal void ActivateCenterPage()
    {
        if (PageCount > 0)
        {
            PageActivated?.Invoke(this, CenterPage);
        }
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new CoverFlowStripAutomationPeer(this);

    /// <summary>Shows a document's thumbnails (or nothing, for <c>null</c>), starting on page 0.</summary>
    public void SetSource(PdfThumbnailSet? source)
    {
        if (_source is not null)
        {
            _source.ThumbnailChanged -= OnThumbnailChanged;
        }

        _source = source;
        if (source is not null)
        {
            source.ThumbnailChanged += OnThumbnailChanged;
        }

        _position = _target = 0;
        _velocity = 0;
        _reportedCenter = -1;
        _baseIndex = int.MinValue;
        Refresh();
    }

    /// <summary>Brings a page to the center - animated, or immediately.</summary>
    public void ScrollToPage(int pageIndex, bool animate = true)
    {
        _target = ClampIndex(pageIndex);
        if (animate)
        {
            StartRendering();
            return;
        }

        _position = _target;
        _velocity = 0;
        Refresh();
    }

    // ---- Layout and pose -------------------------------------------------------------------

    private void OnCanvasSizeChanged(object sender, SizeChangedEventArgs e)
    {
        ItemsCanvas.Clip = new RectangleGeometry { Rect = new Rect(0, 0, e.NewSize.Width, e.NewSize.Height) };
        RecomputeLayout();
        _baseIndex = int.MinValue;
        Refresh();
    }

    private void RecomputeLayout()
    {
        _thumbWidth = Math.Clamp(ItemsCanvas.ActualWidth - 56, 56, 170);
        _thumbBoxHeight = _thumbWidth * 1.4142;
        _gapNear = _thumbBoxHeight * 0.62;
        _gapFar = _thumbBoxHeight * 0.17;
    }

    private void Refresh()
    {
        AssignSlots();
        ApplyPose();
        UpdateCenter();
    }

    /// <summary>Points every slot at the page it should show: slot k shows the page k - Reach away from the centered one.</summary>
    private void AssignSlots()
    {
        var center = ClampIndex((int)Math.Round(_position));
        if (center == _baseIndex)
        {
            return;
        }

        _baseIndex = center;
        for (var k = 0; k < _slots.Length; k++)
        {
            var slot = _slots[k];
            var index = center + k - Reach;
            if (_source is null || index < 0 || index >= _source.Count)
            {
                slot.Index = -1;
                continue;
            }

            ConfigureSlot(slot, index, _source[index]);
        }
    }

    private void ConfigureSlot(Slot slot, int index, PdfThumbnail thumbnail)
    {
        // Fit the page inside a thumbnail-width by page-height box, keeping its shape.
        var width = _thumbWidth;
        var height = width * thumbnail.AspectRatio;
        if (height > _thumbBoxHeight)
        {
            height = _thumbBoxHeight;
            width = height / thumbnail.AspectRatio;
        }

        slot.Index = index;
        slot.Width = Math.Max(1, width);
        slot.Height = Math.Max(1, height);
        slot.Root.Width = slot.Width;
        slot.Root.Height = slot.Height;
        Canvas.SetLeft(slot.Root, (ItemsCanvas.ActualWidth - slot.Width) / 2);
        Canvas.SetTop(slot.Root, (ItemsCanvas.ActualHeight - slot.Height) / 2);

        slot.Picture.Source = thumbnail.Bitmap;
        slot.Number.Text = (index + 1).ToString();
        slot.Number.Visibility = thumbnail.Bitmap is null ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(slot.Root, $"Pagina {index + 1}");
    }

    private readonly record struct Pose(double Offset, double Tilt, double Scale, double Opacity, double Dim);

    /// <param name="distance">Signed distance, in pages, from the (fractional) center position: negative above it, positive below.</param>
    private Pose PoseFor(double distance)
    {
        var far = Math.Abs(distance);
        var side = Math.Sign(distance);
        var near = Math.Min(far, 1); // 0 = exactly centered, 1 = fully "to the side"

        var offset = side * (near * _gapNear + Math.Max(far - 1, 0) * _gapFar);

        // Pages after the centered one (below it) lie like sheets on a table seen from above:
        // their bottom edge, the one nearest the viewer, is the longer one. The pages before it
        // fan out the opposite way (top edge longer), so the strip opens symmetrically around
        // the centered page. A positive plane rotation brings the top edge toward the viewer.
        var tilt = -side * MaxTiltDegrees * near;
        var scale = Math.Max(0.5, 1 - (1 - SideScale) * near - Math.Max(far - 1, 0) * 0.02);
        var opacity = far <= 2 ? 1 : Math.Max(0, 1 - (far - 2) / 4.0);
        return new Pose(offset, tilt, scale, opacity, 0.28 * near);
    }

    private void ApplyPose()
    {
        foreach (var slot in _slots)
        {
            if (slot.Index < 0)
            {
                slot.Root.Visibility = Visibility.Collapsed;
                continue;
            }

            var distance = slot.Index - _position;
            var pose = PoseFor(distance);
            if (pose.Opacity <= 0.01)
            {
                slot.Root.Visibility = Visibility.Collapsed;
                continue;
            }

            slot.Root.Visibility = Visibility.Visible;
            slot.Root.Opacity = pose.Opacity;
            slot.Move.ScaleX = slot.Move.ScaleY = pose.Scale;
            slot.Move.TranslateY = pose.Offset;
            slot.Tilt.RotationX = pose.Tilt;
            slot.Dim.Opacity = pose.Dim;
            Canvas.SetZIndex(slot.Root, 1000 - (int)(Math.Abs(distance) * 100));
        }
    }

    private void UpdateCenter()
    {
        if (_source is null || _source.Count == 0)
        {
            PageLabel.Text = string.Empty;
            return;
        }

        var center = ClampIndex((int)Math.Round(_position));
        PageLabel.Text = $"Pagina {center + 1} di {_source.Count}";
        if (center != _reportedCenter)
        {
            _reportedCenter = center;
            CenterPageChanged?.Invoke(this, center);
        }
    }

    private int ClampIndex(int index) => _source is null || _source.Count == 0 ? 0 : Math.Clamp(index, 0, _source.Count - 1);

    private double ClampIndex(double index) => _source is null || _source.Count == 0 ? 0 : Math.Clamp(index, 0, _source.Count - 1);

    private void OnThumbnailChanged(object? sender, int index)
    {
        foreach (var slot in _slots)
        {
            if (slot.Index == index && _source is not null)
            {
                slot.Picture.Source = _source[index].Bitmap;
                slot.Number.Visibility = Visibility.Collapsed;
            }
        }
    }

    // ---- Animation --------------------------------------------------------------------------

    private void StartRendering()
    {
        if (_rendering)
        {
            return;
        }

        _rendering = true;
        _lastFrameSeconds = _clock.Elapsed.TotalSeconds;
        CompositionTarget.Rendering += OnRendering;
    }

    private void StopRendering()
    {
        if (!_rendering)
        {
            return;
        }

        _rendering = false;
        CompositionTarget.Rendering -= OnRendering;
    }

    private void OnRendering(object? sender, object e)
    {
        var now = _clock.Elapsed.TotalSeconds;
        var dt = Math.Min(now - _lastFrameSeconds, 0.05);
        _lastFrameSeconds = now;

        StepSpring(dt);
        Refresh();

        if (Math.Abs(_position - _target) < 0.0005 && Math.Abs(_velocity) < 0.0005)
        {
            _position = _target;
            _velocity = 0;
            Refresh();
            StopRendering();
        }
    }

    /// <summary>Critically damped spring toward <see cref="_target"/>: fast to start, no overshoot, eases to a stop.</summary>
    private void StepSpring(double dt)
    {
        var omega = 2.0 / SmoothTimeSeconds;
        var x = omega * dt;
        var decay = 1.0 / (1.0 + x + 0.48 * x * x + 0.235 * x * x * x);
        var change = _position - _target;
        var temp = (_velocity + omega * change) * dt;
        _velocity = (_velocity - omega * temp) * decay;
        _position = _target + (change + temp) * decay;
    }

    // ---- Input ------------------------------------------------------------------------------

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        Focus(FocusState.Pointer);
        _pressed = true;
        _dragging = false;
        _pressPoint = e.GetCurrentPoint(ItemsCanvas).Position;
        _dragStartTarget = _target;
        Root.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_pressed)
        {
            return;
        }

        var dy = e.GetCurrentPoint(ItemsCanvas).Position.Y - _pressPoint.Y;
        if (!_dragging && Math.Abs(dy) > 6)
        {
            _dragging = true;
        }

        if (_dragging)
        {
            // Dragging down pulls the earlier pages toward the center.
            _target = ClampIndex(_dragStartTarget - dy / DragPixelsPerPage);
            StartRendering();
        }
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_pressed)
        {
            return;
        }

        var wasDragging = _dragging;
        _pressed = false;
        _dragging = false;
        Root.ReleasePointerCapture(e.Pointer);

        if (wasDragging)
        {
            _target = Math.Round(_target);
            StartRendering();
            return;
        }

        var hit = HitTest(e.GetCurrentPoint(ItemsCanvas).Position);
        if (hit >= 0)
        {
            _target = hit;
            StartRendering();
            PageActivated?.Invoke(this, hit);
        }
    }

    private void OnPointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        if (_pressed && _dragging)
        {
            _target = Math.Round(_target);
            StartRendering();
        }

        _pressed = false;
        _dragging = false;
    }

    private void OnPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        // One page per wheel notch; smooth-scrolling touchpads send many small deltas, so accumulate them.
        _wheelAccumulator += e.GetCurrentPoint(Root).Properties.MouseWheelDelta;
        var steps = (int)(_wheelAccumulator / 120);
        if (steps != 0)
        {
            _wheelAccumulator -= steps * 120;
            _target = ClampIndex(Math.Round(_target) - steps);
            StartRendering();
        }

        e.Handled = true;
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var current = Math.Round(_target);
        switch (e.Key)
        {
            case VirtualKey.Up:
            case VirtualKey.Left:
                _target = ClampIndex(current - 1);
                break;
            case VirtualKey.Down:
            case VirtualKey.Right:
                _target = ClampIndex(current + 1);
                break;
            case VirtualKey.PageUp:
                _target = ClampIndex(current - Reach);
                break;
            case VirtualKey.PageDown:
                _target = ClampIndex(current + Reach);
                break;
            case VirtualKey.Home:
                _target = 0;
                break;
            case VirtualKey.End:
                _target = ClampIndex(int.MaxValue);
                break;
            case VirtualKey.Enter:
            case VirtualKey.Space:
                PageActivated?.Invoke(this, ClampIndex((int)current));
                e.Handled = true;
                return;
            default:
                return;
        }

        StartRendering();
        e.Handled = true;
    }

    /// <summary>The page under <paramref name="point"/> (canvas coordinates), or -1. The pages nearest the center are on top, so they are tried first.</summary>
    private int HitTest(Point point)
    {
        var centerX = ItemsCanvas.ActualWidth / 2;
        var centerY = ItemsCanvas.ActualHeight / 2;

        Slot? best = null;
        var bestDistance = double.MaxValue;
        foreach (var slot in _slots)
        {
            if (slot.Index < 0 || slot.Root.Visibility != Visibility.Visible)
            {
                continue;
            }

            var distance = slot.Index - _position;
            var pose = PoseFor(distance);
            var halfWidth = slot.Width * pose.Scale / 2;
            // A tilted page looks shorter by the cosine of its tilt.
            var halfHeight = slot.Height * pose.Scale * Math.Cos(pose.Tilt * Math.PI / 180) / 2;

            if (Math.Abs(point.X - centerX) <= halfWidth
                && Math.Abs(point.Y - (centerY + pose.Offset)) <= halfHeight
                && Math.Abs(distance) < bestDistance)
            {
                best = slot;
                bestDistance = Math.Abs(distance);
            }
        }

        return best?.Index ?? -1;
    }
}

/// <summary>
/// Exposes the strip to assistive technology (and UI Automation) as a slider over the page
/// numbers: its value is the page in the center, setting it scrolls the strip there, and
/// "invoke" chooses the centered page - the same three things the mouse and keyboard can do.
/// </summary>
internal sealed class CoverFlowStripAutomationPeer : FrameworkElementAutomationPeer, IRangeValueProvider, IInvokeProvider
{
    private readonly CoverFlowStrip _strip;

    public CoverFlowStripAutomationPeer(CoverFlowStrip strip) : base(strip) => _strip = strip;

    protected override string GetClassNameCore() => nameof(CoverFlowStrip);

    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Slider;

    protected override object GetPatternCore(PatternInterface patternInterface)
        => patternInterface is PatternInterface.RangeValue or PatternInterface.Invoke ? this : base.GetPatternCore(patternInterface);

    public bool IsReadOnly => false;

    public double Minimum => 1;

    public double Maximum => Math.Max(1, _strip.PageCount);

    public double Value => _strip.CenterPage + 1;

    public double SmallChange => 1;

    public double LargeChange => 7;

    public void SetValue(double value) => _strip.ScrollToPage((int)Math.Round(value) - 1);

    public void Invoke() => _strip.ActivateCenterPage();
}
