using System.ComponentModel;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using PdfEngine.Annotations;
using PdfEngine.Editing;
using Pdf26.ViewModels;
using Windows.System;

namespace Pdf26.Views;

/// <summary>
/// The interactive layer of one page's lines of text while "Modifica testo" is on (see MainWindow.xaml's page
/// template): a faint frame over each line, which can be selected with a click, edited (double click, Enter or F2:
/// a flyout with the text) and deleted (Delete key or the flyout's button). Like <see cref="PdfAnnotationLayer"/>
/// it has no background, so a click anywhere but on a line passes through to what is underneath. The changes go
/// through <see cref="MainViewModel"/>, which owns the document and the undo history.
/// </summary>
public sealed class PdfTextEditLayer : Canvas
{
    public static readonly DependencyProperty PageProperty = DependencyProperty.Register(
        nameof(Page), typeof(PdfPageViewModel), typeof(PdfTextEditLayer), new PropertyMetadata(null, OnPageChanged));

    private const double MinHitSize = 8;
    private const double DragThreshold = 3;

    /// <summary>How far a line's frame sticks out left of and above its text, in pixels (see <see cref="Rebuild"/>).</summary>
    private const double FrameLeftPad = 2;
    private const double FrameTopPad = 1;

    private readonly Dictionary<int, Border> _boxes = new();

    private PdfTextLine? _dragging;
    private Windows.Foundation.Point _dragStart;
    private double _dragOriginLeft, _dragOriginTop;
    private bool _dragMoved;

    public PdfPageViewModel? Page
    {
        get => (PdfPageViewModel?)GetValue(PageProperty);
        set => SetValue(PageProperty, value);
    }

    private static void OnPageChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var self = (PdfTextEditLayer)d;
        if (e.OldValue is PdfPageViewModel oldPage)
        {
            oldPage.TextLinesChanged -= self.OnTextLinesChanged;
            oldPage.PropertyChanged -= self.OnPagePropertyChanged;
            oldPage.Owner.PropertyChanged -= self.OnOwnerPropertyChanged;
        }

        if (e.NewValue is PdfPageViewModel newPage)
        {
            newPage.TextLinesChanged += self.OnTextLinesChanged;
            newPage.PropertyChanged += self.OnPagePropertyChanged;
            newPage.Owner.PropertyChanged += self.OnOwnerPropertyChanged;
        }

        self.Rebuild();
    }

    private void OnTextLinesChanged(object? sender, EventArgs e) => Rebuild();

    private void OnPagePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // A zoom change resizes the page; every line frame moves and scales with it.
        if (e.PropertyName is nameof(PdfPageViewModel.PixelWidth) or nameof(PdfPageViewModel.PixelHeight))
        {
            Rebuild();
        }
    }

    private void OnOwnerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.IsTextEditTool):
                Rebuild();
                break;
            case nameof(MainViewModel.SelectedTextLine):
                UpdateSelectionVisuals();
                break;
        }
    }

    private void Rebuild()
    {
        // Rebuilding replaces every element: give keyboard focus back to the same line afterwards, so Delete and Enter
        // keep working on a line that was just edited.
        var focusedIndex = XamlRoot is not null && FocusManager.GetFocusedElement(XamlRoot) is Border { Tag: PdfTextLine focused } box && ReferenceEquals(box.Parent, this)
            ? focused.ObjectIndex
            : (int?)null;

        Children.Clear();
        _boxes.Clear();
        if (Page is not { } page || !page.Owner.IsTextEditTool || page.TextLines is not { } lines)
        {
            return;
        }

        var zoom = page.Scale;
        foreach (var line in lines)
        {
            var width = Math.Max(MinHitSize, line.Rect.Width * zoom + 4);
            var height = Math.Max(MinHitSize, line.Rect.Height * zoom + 2);
            var element = new Border
            {
                Width = width,
                Height = height,
                Background = new SolidColorBrush(Colors.Transparent),
                BorderBrush = FaintBrush(),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(2),
                IsTabStop = true,
                UseSystemFocusVisuals = false,
                Tag = line,
            };
            SetLeft(element, line.Rect.Left * zoom - FrameLeftPad);
            SetTop(element, (page.PointHeight - line.Rect.Top) * zoom - FrameTopPad);
            ToolTipService.SetToolTip(element, $"{line.FontName} · {line.FontSize:0.#} pt · doppio clic per modificare, trascina per spostare");
            ToolTipService.SetPlacement(element, PlacementMode.Mouse);
            element.PointerEntered += OnLinePointerEntered;
            element.PointerExited += OnLinePointerExited;
            element.PointerPressed += OnLinePointerPressed;
            element.PointerMoved += OnLinePointerMoved;
            element.PointerReleased += OnLinePointerReleased;
            element.PointerCaptureLost += OnLinePointerCaptureLost;
            element.DoubleTapped += OnLineDoubleTapped;
            element.KeyDown += OnLineKeyDown;
            Children.Add(element);
            _boxes[line.ObjectIndex] = element;
        }

        UpdateSelectionVisuals();
        if (focusedIndex is { } index && _boxes.TryGetValue(index, out var target))
        {
            target.Focus(FocusState.Programmatic);
        }
    }

    private static Brush AccentBrush() => (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];

    private static Brush FaintBrush() => new SolidColorBrush(Windows.UI.Color.FromArgb(0x55, 0x26, 0x83, 0xDB));

    private bool IsSelected(PdfTextLine line)
        => Page is { } page && page.Owner.SelectedTextLine is { } selected
           && ReferenceEquals(selected.Page, page) && selected.ObjectIndex == line.ObjectIndex;

    private void UpdateSelectionVisuals()
    {
        foreach (var box in _boxes.Values)
        {
            var selected = box.Tag is PdfTextLine line && IsSelected(line);
            box.BorderBrush = selected ? AccentBrush() : FaintBrush();
            box.BorderThickness = new Thickness(selected ? 2 : 1);
        }
    }

    // ---- gestures --------------------------------------------------------------------------

    private void OnLinePointerEntered(object sender, PointerRoutedEventArgs e)
    {
        var box = (Border)sender;
        box.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x22, 0x26, 0x83, 0xDB));
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeAll);
    }

    private void OnLinePointerExited(object sender, PointerRoutedEventArgs e)
    {
        ((Border)sender).Background = new SolidColorBrush(Colors.Transparent);
        ProtectedCursor = null;
    }

    private void OnLinePointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var box = (Border)sender;
        if (box.Tag is not PdfTextLine line || Page is not { } page || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        page.Owner.SelectTextLine(page, line);
        box.Focus(FocusState.Pointer);

        // A press may turn into a drag (moving the line) once the pointer travels a few pixels; a click that does not stays a selection.
        _dragging = line;
        _dragMoved = false;
        _dragStart = e.GetCurrentPoint(this).Position;
        _dragOriginLeft = GetLeft(box);
        _dragOriginTop = GetTop(box);
        box.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnLinePointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_dragging is not { } line || Page is not { } page || !_boxes.TryGetValue(line.ObjectIndex, out var box))
        {
            return;
        }

        var position = e.GetCurrentPoint(this).Position;
        var dx = position.X - _dragStart.X;
        var dy = position.Y - _dragStart.Y;
        if (!_dragMoved && Math.Abs(dx) < DragThreshold && Math.Abs(dy) < DragThreshold)
        {
            return;
        }

        _dragMoved = true;
        // The frame sticks out of the text by (FrameLeftPad, FrameTopPad) pixels: it is the text's own corner that snaps to the grid.
        var left = Math.Clamp(Snapped(_dragOriginLeft + dx + FrameLeftPad) - FrameLeftPad, 0, Math.Max(0, page.PixelWidth - box.Width));
        var top = Math.Clamp(Snapped(_dragOriginTop + dy + FrameTopPad) - FrameTopPad, 0, Math.Max(0, page.PixelHeight - box.Height));
        SetLeft(box, left);
        SetTop(box, top);
        e.Handled = true;
    }

    private async void OnLinePointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_dragging is not { } line || Page is not { } page || !_boxes.TryGetValue(line.ObjectIndex, out var box))
        {
            return;
        }

        var moved = _dragMoved;
        _dragging = null;
        ((UIElement)sender).ReleasePointerCapture(e.Pointer);
        e.Handled = true;

        if (!moved)
        {
            return;
        }

        // Pixels to points; the page's y axis points up, the screen's down.
        var zoom = page.Scale;
        var dxPt = (GetLeft(box) - _dragOriginLeft) / zoom;
        var dyPt = -(GetTop(box) - _dragOriginTop) / zoom;
        if (Math.Abs(dxPt) < 0.01 && Math.Abs(dyPt) < 0.01)
        {
            return;
        }

        await page.Owner.MoveTextLineAsync(page, line, dxPt, dyPt);
    }

    private void OnLinePointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        // Capture lost without a release (focus stolen mid-drag): put the frame back where the document has the line.
        if (_dragging is not null)
        {
            _dragging = null;
            Rebuild();
        }
    }

    /// <summary>
    /// With "Aggancia alla griglia" on, a coordinate (pixels from the page's top-left) goes to the nearest grid point - the same grid
    /// <see cref="PdfAnnotationLayer"/> snaps to.
    /// </summary>
    private double Snapped(double pixels)
    {
        if (Page is not { } page || !page.Owner.IsGridActive)
        {
            return pixels;
        }

        var step = PageGrid.StepPt * page.Scale;
        return Math.Round(pixels / step, MidpointRounding.AwayFromZero) * step;
    }

    private void OnLineDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is PdfTextLine line)
        {
            e.Handled = true;
            ShowEditor(line);
        }
    }

    private async void OnLineKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is not PdfTextLine line || Page is not { } page)
        {
            return;
        }

        switch (e.Key)
        {
            case VirtualKey.Delete:
                e.Handled = true;
                await page.Owner.DeleteTextLineAsync(page, line);
                break;
            case VirtualKey.Enter or VirtualKey.F2:
                e.Handled = true;
                ShowEditor(line);
                break;
            case VirtualKey.Escape:
                e.Handled = true;
                page.Owner.SelectTextLine(page, null);
                break;
        }
    }

    /// <summary>
    /// The text of a line in a flyout. Enter or "Applica" replaces it; clicking away or Esc leaves it as it was;
    /// an empty text, or "Elimina riga", deletes the line. When the document's own font is a subset the flyout
    /// says what happens if a typed letter is missing from it.
    /// </summary>
    private void ShowEditor(PdfTextLine line)
    {
        if (Page is not { } page || !_boxes.TryGetValue(line.ObjectIndex, out var box))
        {
            return;
        }

        var text = new TextBox { Text = line.Text, Width = 380, AcceptsReturn = false, SelectionStart = 0 };
        var apply = new Button { Content = "Applica", Style = (Style)Application.Current.Resources["AccentButtonStyle"], MinWidth = 80 };
        var delete = new Button { Content = "Elimina riga" };
        var buttons = new Grid { ColumnSpacing = 8 };
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        buttons.Children.Add(delete);
        Grid.SetColumn(apply, 1);
        buttons.Children.Add(apply);

        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(new TextBlock { Text = "Modifica testo", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        panel.Children.Add(text);
        panel.Children.Add(new TextBlock
        {
            Text = line.IsEmbedded
                ? $"Font del documento: {line.FontName}. Se contiene solo alcuni caratteri, una riga con lettere mancanti viene riscritta con un font simile incorporato dall'app."
                : $"Font del documento: {line.FontName}.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.7,
            FontSize = 12,
            MaxWidth = 380,
        });
        panel.Children.Add(buttons);

        var flyout = new Flyout { Content = panel, Placement = FlyoutPlacementMode.Bottom };
        var action = EditorAction.None;
        apply.Click += (_, _) => { action = EditorAction.Apply; flyout.Hide(); };
        delete.Click += (_, _) => { action = EditorAction.Delete; flyout.Hide(); };
        text.KeyDown += (_, e) =>
        {
            if (e.Key == VirtualKey.Enter)
            {
                e.Handled = true;
                action = EditorAction.Apply;
                flyout.Hide();
            }
        };
        flyout.Opened += (_, _) =>
        {
            text.Focus(FocusState.Programmatic);
            text.SelectAll();
        };
        flyout.Closed += async (_, _) =>
        {
            switch (action)
            {
                case EditorAction.Apply:
                    await page.Owner.ReplaceTextLineAsync(page, line, text.Text);
                    break;
                case EditorAction.Delete:
                    await page.Owner.DeleteTextLineAsync(page, line);
                    break;
            }
        };
        flyout.ShowAt(box);
    }

    private enum EditorAction
    {
        None,
        Apply,
        Delete,
    }
}
