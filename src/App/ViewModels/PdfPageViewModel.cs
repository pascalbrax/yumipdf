using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;

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
    private readonly MainViewModel _owner;

    public PdfPageViewModel(int index, double pointWidth, double pointHeight, double zoom, MainViewModel owner)
    {
        Index = index;
        PointWidth = pointWidth;
        PointHeight = pointHeight;
        _owner = owner;
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

    /// <summary>
    /// Whether this page has its own extractable text - set once by <c>MainViewModel</c>'s
    /// native-text pass (<see cref="PdfEngine.PdfPage.HasExtractableText"/>), which every page
    /// starts without until that pass reaches it. Gates drag-to-select entirely
    /// (<see cref="PdfTextSelectionOverlay"/> in MainWindow.xaml only captures the pointer when
    /// this is true) - a scanned page never supports it, by explicit product decision.
    /// </summary>
    [ObservableProperty]
    public partial bool HasNativeText { get; set; }

    /// <summary>The in-progress or just-finished text-selection highlight - drawn the same way as <see cref="Highlights"/>, in its own collection so a search match and a text selection never fight over the same rectangles.</summary>
    public ObservableCollection<PdfSearchHighlightViewModel> Selection { get; } = new();

    /// <summary>Rects in PDF point space for <see cref="Selection"/> - kept for the same reason as <see cref="_highlightRectsPt"/>.</summary>
    private IReadOnlyList<(double Left, double Bottom, double Right, double Top)> _selectionRectsPt = Array.Empty<(double, double, double, double)>();

    private int _dragAnchorChar = -1;
    private int _dragActiveChar = -1;
    private bool _dragUpdateInFlight;
    private (double X, double Y)? _pendingDragPoint;

    /// <summary>Converts a point within this page's own bitmap (pixels, top-down, as rendered) to PDF point space (points, bottom-up) at the current zoom.</summary>
    private (double X, double Y) ToPagePoint(Point pixelPoint)
    {
        var zoom = _owner.Zoom;
        return (pixelPoint.X / zoom, PointHeight - pixelPoint.Y / zoom);
    }

    /// <summary>Starts (or restarts) a drag-to-select at a pixel point - clears any previous selection first, then does nothing further if this page has no native text or the point does not land on a character.</summary>
    public async void BeginTextSelection(Point pixelPoint)
    {
        ClearTextSelection();
        if (!HasNativeText)
        {
            return;
        }

        var (xPt, yPt) = ToPagePoint(pixelPoint);
        var charIndex = await _owner.HitTestCharAsync(Index, xPt, yPt).ConfigureAwait(true);
        if (charIndex < 0)
        {
            return;
        }

        _dragAnchorChar = charIndex;
        _dragActiveChar = charIndex;
    }

    /// <summary>
    /// Extends the in-progress drag to a new pixel point. Coalesces overlapping calls - a
    /// pointer can raise PointerMoved faster than one native round trip completes, and always
    /// resolving against the *latest* point once the in-flight one finishes (rather than
    /// starting a new round trip per event) means a burst of moves never risks an older, slower
    /// call overwriting a newer selection out of order.
    /// </summary>
    public void ExtendTextSelection(Point pixelPoint)
    {
        if (_dragAnchorChar < 0)
        {
            return;
        }

        if (_dragUpdateInFlight)
        {
            _pendingDragPoint = (pixelPoint.X, pixelPoint.Y);
            return;
        }

        _ = RunExtendAsync(pixelPoint);
    }

    private async Task RunExtendAsync(Point pixelPoint)
    {
        _dragUpdateInFlight = true;
        try
        {
            while (true)
            {
                if (_dragAnchorChar < 0)
                {
                    return;
                }

                var (xPt, yPt) = ToPagePoint(pixelPoint);
                var (charIndex, rects) = await _owner.HitTestAndSelectAsync(Index, _dragAnchorChar, xPt, yPt).ConfigureAwait(true);
                if (charIndex >= 0 && _dragAnchorChar >= 0)
                {
                    _dragActiveChar = charIndex;
                    _selectionRectsPt = rects;
                    RebuildSelection();
                }

                if (_pendingDragPoint is not { } next)
                {
                    return;
                }

                _pendingDragPoint = null;
                pixelPoint = new Point(next.X, next.Y);
            }
        }
        finally
        {
            _dragUpdateInFlight = false;
        }
    }

    /// <summary>
    /// Ends the drag and returns the selected text - empty (and the highlight cleared) if the
    /// drag never moved off its starting character, matching normal desktop text-selection UX
    /// where a plain click just places a caret rather than selecting anything.
    /// </summary>
    public async Task<string> EndTextSelectionAsync()
    {
        if (_dragAnchorChar < 0 || _dragActiveChar < 0 || _dragAnchorChar == _dragActiveChar)
        {
            ClearTextSelection();
            return string.Empty;
        }

        return await _owner.ExtractSelectionTextAsync(Index, _dragAnchorChar, _dragActiveChar).ConfigureAwait(true);
    }

    /// <summary>
    /// Called by <see cref="PdfTextSelectionOverlay"/> right after it copies a just-finished
    /// selection's text to the clipboard - clears the highlight (a copied selection does not
    /// stay highlighted) and confirms via the status bar, the same mechanism <c>MainViewModel</c>
    /// already uses for other one-off confirmations (see <c>InfoMessage</c>).
    /// </summary>
    public void ReportTextCopied()
    {
        ClearTextSelection();
        _owner.InfoMessage = "Testo copiato negli appunti.";
    }

    /// <summary>Clears the selection highlight and drag state - called on a new drag, right after a copy, or when the page's native-text status changes.</summary>
    public void ClearTextSelection()
    {
        _dragAnchorChar = -1;
        _dragActiveChar = -1;
        _pendingDragPoint = null;
        if (_selectionRectsPt.Count > 0)
        {
            _selectionRectsPt = Array.Empty<(double, double, double, double)>();
            Selection.Clear();
        }
    }

    private void RebuildSelection()
    {
        var zoom = _owner.Zoom;
        Selection.Clear();
        foreach (var (left, bottom, right, top) in _selectionRectsPt)
        {
            var margin = new Thickness(left * zoom, (PointHeight - top) * zoom, 0, 0);
            var width = Math.Max(1, (right - left) * zoom);
            var height = Math.Max(1, (top - bottom) * zoom);
            Selection.Add(new PdfSearchHighlightViewModel(margin, width, height));
        }
    }

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

        if (_selectionRectsPt.Count > 0)
        {
            RebuildSelection();
        }
    }

    private static double ToPixels(double points, double zoom) => Math.Max(1, points * zoom);
}
