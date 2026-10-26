using System.ComponentModel;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using PdfEngine.Annotations;
using Pdf26.ViewModels;
using Windows.Foundation;
using Windows.System;

namespace Pdf26.Views;

/// <summary>
/// The interactive layer of one page's notes and stamps (see MainWindow.xaml's page template):
/// a transparent hit area over each annotation, so it can be selected (framed), dragged to move,
/// resized by its corner handle (stamps; the proportions are kept), edited (a note's text
/// opens in a flyout) and deleted (Delete key or right-click menu) - and, while a placement tool
/// is active, a click anywhere on the page puts the new annotation there. The pictures themselves
/// are in the page bitmap, drawn by PDFium; this layer only owns the gestures. Everything that
/// touches the document goes through <see cref="MainViewModel"/>.
/// </summary>
/// <remarks>
/// With no placement tool active the layer has no background, so clicks anywhere but on an
/// annotation pass through to the text selection and form fields underneath it.
/// </remarks>
public sealed class PdfAnnotationLayer : Canvas
{
    public static readonly DependencyProperty PageProperty = DependencyProperty.Register(
        nameof(Page), typeof(PdfPageViewModel), typeof(PdfAnnotationLayer), new PropertyMetadata(null, OnPageChanged));

    private const double HandleSize = 12;
    private const double MinHitSize = 14;
    private const double DragThreshold = 3;

    private readonly Dictionary<PageAnnotationViewModel, Border> _boxes = new();
    private readonly Dictionary<PageAnnotationViewModel, Border> _handles = new();

    private PageAnnotationViewModel? _dragging;
    private Point _dragStart;
    private double _dragOriginLeft, _dragOriginTop, _dragWidth, _dragHeight;
    private bool _dragMoved;
    private bool _resizing;

    public PdfPageViewModel? Page
    {
        get => (PdfPageViewModel?)GetValue(PageProperty);
        set => SetValue(PageProperty, value);
    }

    public PdfAnnotationLayer()
    {
        PointerPressed += OnLayerPointerPressed;
    }

    private static void OnPageChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var self = (PdfAnnotationLayer)d;
        if (e.OldValue is PdfPageViewModel oldPage)
        {
            oldPage.AnnotationsChanged -= self.OnAnnotationsChanged;
            oldPage.PropertyChanged -= self.OnPagePropertyChanged;
            oldPage.Owner.PropertyChanged -= self.OnOwnerPropertyChanged;
            oldPage.Owner.NoteEditRequested -= self.OnNoteEditRequested;
            oldPage.Owner.FieldEditRequested -= self.OnFieldEditRequested;
        }

        if (e.NewValue is PdfPageViewModel newPage)
        {
            newPage.AnnotationsChanged += self.OnAnnotationsChanged;
            newPage.PropertyChanged += self.OnPagePropertyChanged;
            newPage.Owner.PropertyChanged += self.OnOwnerPropertyChanged;
            newPage.Owner.NoteEditRequested += self.OnNoteEditRequested;
            newPage.Owner.FieldEditRequested += self.OnFieldEditRequested;
        }

        self.Rebuild();
        self.UpdatePlacementMode();
    }

    private void OnAnnotationsChanged(object? sender, EventArgs e) => Rebuild();

    private void OnPagePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // A zoom change resizes the page; every annotation moves and scales with it.
        if (e.PropertyName is nameof(PdfPageViewModel.PixelWidth) or nameof(PdfPageViewModel.PixelHeight))
        {
            Rebuild();
        }
    }

    private void OnOwnerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.ActiveAnnotationTool):
                UpdatePlacementMode();
                break;
            case nameof(MainViewModel.SelectedAnnotation):
                UpdateSelectionVisuals();
                break;
        }
    }

    // ---- placement ---------------------------------------------------------------------

    private bool IsPlacing => Page?.Owner.IsPlacingAnnotation == true;

    /// <summary>While a tool waits for a click the whole page takes it (a transparent background makes the layer hit-testable); otherwise only the annotations do.</summary>
    private void UpdatePlacementMode()
    {
        var placing = IsPlacing;
        Background = placing ? new SolidColorBrush(Colors.Transparent) : null;
        ProtectedCursor = placing ? InputSystemCursor.Create(InputSystemCursorShape.Cross) : null;
        foreach (var box in _boxes.Values)
        {
            box.IsHitTestVisible = !placing;
        }

        foreach (var handle in _handles.Values)
        {
            handle.IsHitTestVisible = !placing;
        }
    }

    private async void OnLayerPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!IsPlacing || Page is not { } page || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        e.Handled = true;
        var point = e.GetCurrentPoint(this).Position;
        var (xPt, yPt) = ToPagePoint(page, point.X, point.Y);
        await page.Owner.PlaceAnnotationAsync(page.Index, xPt, yPt);
    }

    // ---- building ----------------------------------------------------------------------

    private static (double X, double Y) ToPagePoint(PdfPageViewModel page, double xPx, double yPx)
    {
        var zoom = page.Owner.Zoom;
        return (xPx / zoom, page.PointHeight - yPx / zoom);
    }

    private void Rebuild()
    {
        // A rebuild replaces every element, so remember whether keyboard focus was on an annotation's box
        // (it is after a click or a drag) and give it back to the same annotation afterwards: Delete and
        // Enter keep working on a freshly moved or resized annotation.
        var focusedKey = FocusedKey();

        Children.Clear();
        _boxes.Clear();
        _handles.Clear();
        _dragging = null;
        if (Page is not { } page)
        {
            return;
        }

        var zoom = page.Owner.Zoom;
        foreach (var annotation in page.Annotations.Concat(page.DesignFields))
        {
            var width = Math.Max(MinHitSize, annotation.Rect.Width * zoom);
            var height = Math.Max(MinHitSize, annotation.Rect.Height * zoom);
            var left = annotation.Rect.Left * zoom;
            var top = (page.PointHeight - annotation.Rect.Top) * zoom;

            var box = new Border
            {
                Width = width,
                Height = height,
                Background = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(2),
                CornerRadius = new CornerRadius(2),
                IsTabStop = true,
                UseSystemFocusVisuals = false,
                Tag = annotation,
                IsHitTestVisible = !IsPlacing,
            };
            SetLeft(box, left);
            SetTop(box, top);
            if (annotation.Field is { } designField)
            {
                box.Child = BuildFieldPlaceholder(designField);
            }

            ToolTipService.SetToolTip(box, annotation.Field is { } named ? named.Name : annotation.IsNote && annotation.Contents.Length > 0 ? annotation.Contents : null);
            ToolTipService.SetPlacement(box, PlacementMode.Mouse);
            box.PointerPressed += OnBoxPointerPressed;
            box.PointerMoved += OnBoxPointerMoved;
            box.PointerReleased += OnBoxPointerReleased;
            box.PointerCaptureLost += OnBoxPointerCaptureLost;
            box.PointerEntered += OnBoxPointerEntered;
            box.PointerExited += OnBoxPointerExited;
            box.DoubleTapped += OnBoxDoubleTapped;
            box.KeyDown += OnBoxKeyDown;
            box.ContextFlyout = BuildMenu(annotation);
            Children.Add(box);
            _boxes[annotation] = box;

            if (annotation.CanResize)
            {
                // The corner handle: dragging it scales the stamp. Notes have a fixed icon size, and a stamp
                // made elsewhere cannot be enlarged reliably (PdfEditableAnnotations remarks), so no handle there.
                var handle = new Border
                {
                    Width = HandleSize,
                    Height = HandleSize,
                    Background = new SolidColorBrush(Colors.White),
                    BorderBrush = AccentBrush(),
                    BorderThickness = new Thickness(2),
                    CornerRadius = new CornerRadius(2),
                    Visibility = Visibility.Collapsed,
                    Tag = annotation,
                    IsHitTestVisible = !IsPlacing,
                };
                PositionHandle(handle, left, top, width, height);
                handle.PointerPressed += OnHandlePointerPressed;
                handle.PointerMoved += OnHandlePointerMoved;
                handle.PointerReleased += OnBoxPointerReleased;
                handle.PointerCaptureLost += OnBoxPointerCaptureLost;
                handle.PointerEntered += (_, _) => ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeNorthwestSoutheast);
                handle.PointerExited += (_, _) => ProtectedCursor = null;
                Children.Add(handle);
                _handles[annotation] = handle;
            }
        }

        UpdateSelectionVisuals();

        if (focusedKey is not null
            && page.Annotations.Concat(page.DesignFields).FirstOrDefault(a => Equals(KeyOf(a), focusedKey)) is { } same
            && _boxes.TryGetValue(same, out var focusTarget))
        {
            focusTarget.Focus(FocusState.Programmatic);
        }
    }

    /// <summary>What identifies an item across a rebuild: the design field itself, or the annotation's index in its page.</summary>
    private static object KeyOf(PageAnnotationViewModel annotation) => annotation.Field is { } field ? field : annotation.Index;

    private object? FocusedKey()
    {
        if (XamlRoot is null)
        {
            return null;
        }

        return FocusManager.GetFocusedElement(XamlRoot) is Border { Tag: PageAnnotationViewModel annotation } focused && ReferenceEquals(focused.Parent, this)
            ? KeyOf(annotation)
            : null;
    }

    /// <summary>
    /// A field that is not in the document yet is not in the page bitmap either, so its box draws
    /// what the field will look like: a white box with a thin frame, and for a text field its name.
    /// </summary>
    private static UIElement BuildFieldPlaceholder(DesignField field)
    {
        var frame = new Border
        {
            Background = new SolidColorBrush(Colors.White),
            BorderBrush = new SolidColorBrush(Colors.Black),
            BorderThickness = new Thickness(1),
            IsHitTestVisible = false,
        };
        if (field.Kind == PdfEngine.Forms.PdfFieldKind.Text)
        {
            frame.Child = new TextBlock
            {
                Text = field.Name,
                FontSize = 10,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0xA0, 0x40, 0x40, 0x40)),
                Margin = new Thickness(3, 0, 3, 0),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
        }

        return frame;
    }

    private static void PositionHandle(Border handle, double left, double top, double width, double height)
    {
        SetLeft(handle, left + width - HandleSize / 2);
        SetTop(handle, top + height - HandleSize / 2);
    }

    private static Brush AccentBrush() => (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];

    private MenuFlyout BuildMenu(PageAnnotationViewModel annotation)
    {
        var menu = new MenuFlyout();
        if (annotation.IsField)
        {
            var properties = new MenuFlyoutItem { Text = "Proprietà del campo...", Icon = new SymbolIcon(Symbol.Edit) };
            properties.Click += (_, _) => ShowFieldEditor(annotation);
            menu.Items.Add(properties);
        }

        if (annotation.IsNote)
        {
            var edit = new MenuFlyoutItem { Text = "Modifica testo...", Icon = new SymbolIcon(Symbol.Edit) };
            edit.Click += (_, _) => ShowNoteEditor(annotation);
            menu.Items.Add(edit);
        }

        var delete = new MenuFlyoutItem { Text = annotation.IsField ? "Elimina campo" : annotation.IsNote ? "Elimina nota" : "Elimina timbro", Icon = new SymbolIcon(Symbol.Delete) };
        delete.Click += async (_, _) => await annotation.Page.Owner.DeleteAnnotationAsync(annotation);
        menu.Items.Add(delete);
        return menu;
    }

    /// <summary>Selected: framed in the accent color, with its corner handle. Hovered: a faint frame.</summary>
    private void UpdateSelectionVisuals()
    {
        var selected = Page?.Owner.SelectedAnnotation;
        foreach (var (annotation, box) in _boxes)
        {
            var isSelected = ReferenceEquals(annotation, selected);
            box.BorderBrush = isSelected ? AccentBrush() : null;
            if (_handles.TryGetValue(annotation, out var handle))
            {
                handle.Visibility = isSelected ? Visibility.Visible : Visibility.Collapsed;
            }
        }
    }

    // ---- selecting and dragging --------------------------------------------------------

    private void OnBoxPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        var box = (Border)sender;
        if (box.Tag is PageAnnotationViewModel annotation && !ReferenceEquals(annotation, Page?.Owner.SelectedAnnotation))
        {
            box.BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0x88, 0x80, 0x80, 0x80));
        }

        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeAll);
    }

    private void OnBoxPointerExited(object sender, PointerRoutedEventArgs e)
    {
        var box = (Border)sender;
        if (box.Tag is PageAnnotationViewModel annotation && !ReferenceEquals(annotation, Page?.Owner.SelectedAnnotation))
        {
            box.BorderBrush = null;
        }

        ProtectedCursor = null;
    }

    private void OnBoxPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var box = (Border)sender;
        if (box.Tag is not PageAnnotationViewModel annotation || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        annotation.Page.Owner.SelectAnnotation(annotation);
        box.Focus(FocusState.Pointer);
        BeginDrag(annotation, box, e, resizing: false);
    }

    private void OnHandlePointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var handle = (Border)sender;
        if (handle.Tag is not PageAnnotationViewModel annotation || !_boxes.ContainsKey(annotation) || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        BeginDrag(annotation, handle, e, resizing: true);
    }

    private void BeginDrag(PageAnnotationViewModel annotation, UIElement source, PointerRoutedEventArgs e, bool resizing)
    {
        var box = _boxes[annotation];
        _dragging = annotation;
        _resizing = resizing;
        _dragMoved = false;
        _dragStart = e.GetCurrentPoint(this).Position;
        _dragOriginLeft = GetLeft(box);
        _dragOriginTop = GetTop(box);
        _dragWidth = box.Width;
        _dragHeight = box.Height;
        source.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnBoxPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_dragging is not { } annotation || _resizing || Page is not { } page)
        {
            return;
        }

        var delta = e.GetCurrentPoint(this).Position;
        var dx = delta.X - _dragStart.X;
        var dy = delta.Y - _dragStart.Y;
        if (!_dragMoved && Math.Abs(dx) < DragThreshold && Math.Abs(dy) < DragThreshold)
        {
            return;
        }

        _dragMoved = true;
        var box = _boxes[annotation];
        var left = Math.Clamp(Snapped(_dragOriginLeft + dx), 0, Math.Max(0, page.PixelWidth - box.Width));
        var top = Math.Clamp(Snapped(_dragOriginTop + dy), 0, Math.Max(0, page.PixelHeight - box.Height));
        SetLeft(box, left);
        SetTop(box, top);
        if (_handles.TryGetValue(annotation, out var handle))
        {
            PositionHandle(handle, left, top, box.Width, box.Height);
        }

        e.Handled = true;
    }

    /// <summary>
    /// With "Aggancia alla griglia" on, a coordinate (pixels from the page's top-left) goes to the nearest grid point: the same
    /// grid <see cref="PdfGridLayer"/> draws and <see cref="PageGrid"/> defines in PDF points, scaled by the zoom. The dragged corner
    /// is what snaps: the top-left one when moving, the bottom-right one when resizing.
    /// </summary>
    private double Snapped(double pixels)
    {
        if (Page is not { } page || !page.Owner.IsGridActive)
        {
            return pixels;
        }

        var step = PageGrid.StepPt * page.Owner.Zoom;
        return Math.Round(pixels / step, MidpointRounding.AwayFromZero) * step;
    }

    private void OnHandlePointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_dragging is not { } annotation || !_resizing || Page is not { } page)
        {
            return;
        }

        var box = _boxes[annotation];
        var point = e.GetCurrentPoint(this).Position;
        _dragMoved = true;

        if (!annotation.KeepProportions)
        {
            // A text field stretches freely: the corner follows the pointer in both directions.
            box.Width = Math.Clamp(Snapped(point.X) - _dragOriginLeft, 16, Math.Max(16, page.PixelWidth - _dragOriginLeft));
            box.Height = Math.Clamp(Snapped(point.Y) - _dragOriginTop, 12, Math.Max(12, page.PixelHeight - _dragOriginTop));
            if (_handles.TryGetValue(annotation, out var freeHandle))
            {
                PositionHandle(freeHandle, _dragOriginLeft, _dragOriginTop, box.Width, box.Height);
            }

            e.Handled = true;
            return;
        }

        // Scale about the top-left corner, keeping the proportions: the width follows the pointer.
        var ratio = _dragHeight / _dragWidth;
        var maxWidth = Math.Min(page.PixelWidth - _dragOriginLeft, (page.PixelHeight - _dragOriginTop) / ratio);
        var width = Math.Clamp(Snapped(point.X) - _dragOriginLeft, 16, Math.Max(16, maxWidth));
        box.Width = width;
        box.Height = width * ratio;
        if (_handles.TryGetValue(annotation, out var handle))
        {
            PositionHandle(handle, _dragOriginLeft, _dragOriginTop, box.Width, box.Height);
        }

        e.Handled = true;
    }

    private async void OnBoxPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_dragging is not { } annotation || Page is not { } page)
        {
            return;
        }

        var moved = _dragMoved;
        var box = _boxes[annotation];
        _dragging = null;
        ((UIElement)sender).ReleasePointerCapture(e.Pointer);
        e.Handled = true;

        if (!moved)
        {
            return;
        }

        var (leftPt, topPt) = ToPagePoint(page, GetLeft(box), GetTop(box));
        var zoom = page.Owner.Zoom;
        var rect = new PdfRect(leftPt, topPt - box.Height / zoom, leftPt + box.Width / zoom, topPt);
        await page.Owner.MoveAnnotationAsync(annotation, rect);
    }

    private void OnBoxPointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        // Capture lost without a release (focus stolen mid-drag): put the picture back where the document has it.
        if (_dragging is not null)
        {
            _dragging = null;
            Rebuild();
        }
    }

    // ---- keyboard, double click, note text ---------------------------------------------

    private async void OnBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is not PageAnnotationViewModel annotation)
        {
            return;
        }

        switch (e.Key)
        {
            case VirtualKey.Delete:
                e.Handled = true;
                await annotation.Page.Owner.DeleteAnnotationAsync(annotation);
                break;
            case VirtualKey.Enter or VirtualKey.F2 when annotation.IsNote:
                e.Handled = true;
                ShowNoteEditor(annotation);
                break;
            case VirtualKey.Enter or VirtualKey.F2 when annotation.IsField:
                e.Handled = true;
                ShowFieldEditor(annotation);
                break;
        }
    }

    private void OnBoxDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is PageAnnotationViewModel { IsNote: true } annotation)
        {
            e.Handled = true;
            ShowNoteEditor(annotation);
        }
        else if (((FrameworkElement)sender).Tag is PageAnnotationViewModel { IsField: true } field)
        {
            e.Handled = true;
            ShowFieldEditor(field);
        }
    }

    private void OnFieldEditRequested(PageAnnotationViewModel annotation)
    {
        if (ReferenceEquals(annotation.Page, Page))
        {
            ShowFieldEditor(annotation);
        }
    }

    /// <summary>
    /// The properties of a form field in a flyout: name, tooltip, required, read-only, and what is
    /// specific to its kind. OK (or clicking away) applies them; a name that is empty or already
    /// taken keeps the flyout open at OK and is simply not applied if the flyout is dismissed instead.
    /// "Elimina campo" removes the field.
    /// </summary>
    private void ShowFieldEditor(PageAnnotationViewModel item)
    {
        if (item.Field is not { } field || !_boxes.TryGetValue(item, out var box) || item.Page.Owner.IsPlacingAnnotation)
        {
            return;
        }

        var isText = field.Kind == PdfEngine.Forms.PdfFieldKind.Text;
        var name = new TextBox { Header = "Nome del campo", Text = field.Name, Width = 280 };
        var error = new TextBlock
        {
            Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"],
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
        };
        var tooltip = new TextBox { Header = "Suggerimento (facoltativo)", Text = field.Tooltip, Width = 280 };
        var required = new CheckBox { Content = "Obbligatorio", IsChecked = field.Required };
        var readOnly = new CheckBox { Content = "Sola lettura", IsChecked = field.ReadOnly };
        var multiline = new CheckBox { Content = "Su più righe", IsChecked = field.Multiline };
        var size = new NumberBox
        {
            Header = "Dimensione del carattere (0 = automatica)",
            Value = field.FontSize,
            Minimum = 0,
            Maximum = 96,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            Width = 280,
        };
        var ticked = new CheckBox { Content = "Spuntata di default", IsChecked = field.CheckedByDefault };

        var delete = new Button { Content = "Elimina campo" };
        var ok = new Button { Content = "OK", Style = (Style)Application.Current.Resources["AccentButtonStyle"], MinWidth = 72 };
        var buttons = new Grid { ColumnSpacing = 8 };
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        buttons.Children.Add(delete);
        Grid.SetColumn(ok, 1);
        buttons.Children.Add(ok);

        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(new TextBlock { Text = isText ? "Campo di testo" : "Casella di controllo", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        panel.Children.Add(name);
        panel.Children.Add(error);
        panel.Children.Add(tooltip);
        panel.Children.Add(required);
        panel.Children.Add(readOnly);
        if (isText)
        {
            panel.Children.Add(multiline);
            panel.Children.Add(size);
        }
        else
        {
            panel.Children.Add(ticked);
        }

        panel.Children.Add(buttons);

        string? Apply()
            => item.Page.Owner.UpdateDesignField(item, new DesignFieldProperties(
                name.Text, tooltip.Text, required.IsChecked == true, readOnly.IsChecked == true,
                multiline.IsChecked == true, double.IsNaN(size.Value) ? 0 : size.Value, ticked.IsChecked == true));

        var finished = false;
        var deleted = false;
        var flyout = new Flyout { Content = panel, Placement = FlyoutPlacementMode.Right };
        ok.Click += (_, _) =>
        {
            if (Apply() is { } problem)
            {
                error.Text = problem;
                error.Visibility = Visibility.Visible;
                return;
            }

            finished = true;
            flyout.Hide();
        };
        delete.Click += (_, _) =>
        {
            deleted = true;
            flyout.Hide();
        };
        flyout.Closed += async (_, _) =>
        {
            if (deleted)
            {
                await item.Page.Owner.DeleteAnnotationAsync(item);
            }
            else if (!finished)
            {
                _ = Apply(); // dismissed: keep what is valid, drop what is not
            }
        };
        flyout.Opened += (_, _) =>
        {
            name.Focus(FocusState.Programmatic);
            name.SelectAll();
        };

        flyout.ShowAt(box);
    }

    private void OnNoteEditRequested(PageAnnotationViewModel annotation)
    {
        if (ReferenceEquals(annotation.Page, Page))
        {
            ShowNoteEditor(annotation);
        }
    }

    /// <summary>A small flyout with the note's text. Closing it (clicking away, Esc, OK) saves what was typed; "Elimina nota" removes the note instead.</summary>
    private void ShowNoteEditor(PageAnnotationViewModel annotation)
    {
        if (!_boxes.TryGetValue(annotation, out var box) || annotation.Page.Owner.IsPlacingAnnotation)
        {
            return;
        }

        var text = new TextBox
        {
            Text = annotation.Contents,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Width = 280,
            MinHeight = 96,
            MaxHeight = 240,
            PlaceholderText = "Scrivi la nota...",
        };
        var delete = new Button { Content = "Elimina nota" };
        var ok = new Button { Content = "OK", Style = (Style)Application.Current.Resources["AccentButtonStyle"], MinWidth = 72 };
        var buttons = new Grid { ColumnSpacing = 8 };
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        buttons.Children.Add(delete);
        Grid.SetColumn(ok, 1);
        buttons.Children.Add(ok);

        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(text);
        panel.Children.Add(buttons);

        var deleted = false;
        var flyout = new Flyout { Content = panel, Placement = FlyoutPlacementMode.Right };
        ok.Click += (_, _) => flyout.Hide();
        delete.Click += (_, _) =>
        {
            deleted = true;
            flyout.Hide();
        };
        var wasEmpty = annotation.Contents.Length == 0;
        flyout.Closed += async (_, _) =>
        {
            // A note with nothing in it is not worth keeping: a freshly placed one that was left blank goes away.
            if (deleted || (wasEmpty && string.IsNullOrWhiteSpace(text.Text)))
            {
                await annotation.Page.Owner.DeleteAnnotationAsync(annotation);
            }
            else
            {
                await annotation.Page.Owner.SetNoteTextAsync(annotation, text.Text);
            }
        };
        flyout.Opened += (_, _) =>
        {
            text.Focus(FocusState.Programmatic);
            text.SelectAll();
        };

        flyout.ShowAt(box);
    }
}
