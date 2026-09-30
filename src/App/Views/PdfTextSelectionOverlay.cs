using System.ComponentModel;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Pdf26.ViewModels;

namespace Pdf26.Views;

/// <summary>
/// Transparent layer drawn over a page's bitmap (see MainWindow.xaml's page DataTemplate) that
/// turns a mouse drag into a native-text selection. On release, a non-empty selection is copied
/// to the clipboard immediately, deselected, and confirmed via the status bar
/// (<see cref="PdfPageViewModel.ReportTextCopied"/>) - no confirmation UI of its own. All the
/// PDFium-backed work (hit-testing, building the highlight rectangles, extracting the selected
/// text) lives in <see cref="PdfPageViewModel"/>/<c>MainViewModel</c> - this control only owns
/// the pointer gesture and the clipboard write.
/// </summary>
/// <remarks>
/// Only hit-test visible while <see cref="PdfPageViewModel.HasNativeText"/> is true, so a
/// scanned page's pointer events pass straight through it (nothing currently needs them there,
/// but this keeps a non-selectable page from ever intercepting a click) - and stays that way
/// live if the page's native-text status only becomes known after this control already exists,
/// since indexing runs in the background after the document opens.
/// </remarks>
public sealed class PdfTextSelectionOverlay : Panel
{
    public static readonly DependencyProperty PageProperty = DependencyProperty.Register(
        nameof(Page), typeof(PdfPageViewModel), typeof(PdfTextSelectionOverlay),
        new PropertyMetadata(null, OnPageChanged));

    public PdfPageViewModel? Page
    {
        get => (PdfPageViewModel?)GetValue(PageProperty);
        set => SetValue(PageProperty, value);
    }

    private bool _dragging;

    public PdfTextSelectionOverlay()
    {
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        IsHitTestVisible = false;

        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
        PointerExited += OnPointerExited;
        PointerCaptureLost += (_, _) => _dragging = false;
    }

    private static void OnPageChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var self = (PdfTextSelectionOverlay)d;
        if (e.OldValue is PdfPageViewModel oldPage)
        {
            oldPage.PropertyChanged -= self.OnPagePropertyChanged;
        }

        if (e.NewValue is PdfPageViewModel newPage)
        {
            newPage.PropertyChanged += self.OnPagePropertyChanged;
        }

        self.UpdateHitTestVisibility();
    }

    private void OnPagePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PdfPageViewModel.HasNativeText))
        {
            UpdateHitTestVisibility();
        }
    }

    private void UpdateHitTestVisibility() => IsHitTestVisible = Page?.HasNativeText == true;

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (Page is not { HasNativeText: true } page || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        _dragging = true;
        CapturePointer(e.Pointer);
        page.BeginTextSelection(e.GetCurrentPoint(this).Position);
        e.Handled = true;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (Page is not { HasNativeText: true } page)
        {
            return;
        }

        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.IBeam);

        if (_dragging)
        {
            page.ExtendTextSelection(e.GetCurrentPoint(this).Position);
            e.Handled = true;
        }
    }

    private async void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging || Page is not { } page)
        {
            return;
        }

        _dragging = false;
        ReleasePointerCapture(e.Pointer);

        var text = await page.EndTextSelectionAsync();
        if (!string.IsNullOrEmpty(text))
        {
            var package = new DataPackage();
            package.SetText(text);
            Clipboard.SetContent(package);
            page.ReportTextCopied();
        }

        e.Handled = true;
    }

    private void OnPointerExited(object sender, PointerRoutedEventArgs e)
    {
        ProtectedCursor = null;
    }
}
