using System.Collections.ObjectModel;
using System.Runtime.InteropServices.WindowsRuntime;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.UI;
using Pdf26.Services;
using PdfEngine;

namespace Pdf26.ViewModels;

public partial class MainViewModel : ObservableObject, IDisposable
{
    /// <summary>Pixels-per-PDF-point at 100% zoom (96 dpi screen / 72 pt-per-inch).</summary>
    private const double BaseZoom = 96.0 / 72.0;
    private const double MinZoom = BaseZoom * 0.2;
    private const double MaxZoom = BaseZoom * 6.0;

    /// <summary>
    /// Upper bound on a single rendered page's pixel width/height, comfortably under the
    /// ~16384px maximum texture dimension most Direct3D hardware enforces (WriteableBitmap
    /// is backed by one). Documents with physically huge pages (e.g. architectural drawings -
    /// this limit was added after a real one with ~40x24 inch pages crashed the app at high
    /// zoom) get their maximum zoom clamped below <see cref="MaxZoom"/> accordingly - see
    /// <see cref="ComputeMaxZoomForDocument"/>.
    /// </summary>
    private const double SafeMaxPixelDimension = 8192;

    private readonly FileSourceResolver _sourceResolver = new();
    private readonly SettingsService _settingsService = new();
    private PdfDocument? _document;
    private PdfSource? _lastResolvedSource;
    private string? _lastResolvedLocalPath;
    private CancellationTokenSource? _renderCts;
    private Task _renderTask = Task.CompletedTask;
    private double _maxZoomForDocument = MaxZoom;

    public ObservableCollection<PdfPageViewModel> Pages { get; } = new();

    [ObservableProperty]
    public partial int CurrentPageIndex { get; set; }

    [ObservableProperty]
    public partial int PageCount { get; set; }

    [ObservableProperty]
    public partial string? DocumentTitle { get; set; }

    [ObservableProperty]
    public partial string? StatusMessage { get; set; }

    [ObservableProperty]
    public partial SolidColorBrush PageAreaBackground { get; set; } = new(Colors.Gray);

    [ObservableProperty]
    public partial double Zoom { get; set; } = BaseZoom;

    public int ZoomPercent => (int)Math.Round(Zoom / BaseZoom * 100);

    public string ZoomPercentText => $"{ZoomPercent}%";

    public MainViewModel()
    {
        PageAreaBackground = new SolidColorBrush(ColorHex.Parse(_settingsService.Load().PageAreaBackgroundHex));
    }

    public void UpdateBackgroundColor(Color color)
    {
        PageAreaBackground = new SolidColorBrush(color);
        _settingsService.Save(new AppSettings { PageAreaBackgroundHex = ColorHex.ToHex(color) });
    }

    public async Task<OpenOutcome> OpenAsync(PdfSource source, string? password = null, CancellationToken cancellationToken = default)
    {
        StatusMessage = $"Apertura di {source.Value}...";
        try
        {
            // Reuse the already-resolved local path on a password retry so a remote source
            // is not re-downloaded for every failed password attempt.
            string localPath;
            if (password is not null && _lastResolvedSource == source && _lastResolvedLocalPath is not null)
            {
                localPath = _lastResolvedLocalPath;
            }
            else
            {
                localPath = await _sourceResolver.ResolveToLocalPathAsync(source, cancellationToken).ConfigureAwait(false);
                _lastResolvedSource = source;
                _lastResolvedLocalPath = localPath;
            }

            PdfDocument document;
            try
            {
                document = PdfDocument.OpenFromPath(localPath, password);
            }
            catch (PdfException ex) when (ex.ErrorCode == PdfErrorCode.Password)
            {
                StatusMessage = password is null ? "Documento protetto da password." : "Password non corretta.";
                return OpenOutcome.PasswordRequired;
            }

            _document?.Dispose();
            _document = document;

            DocumentTitle = Path.GetFileName(localPath);
            PageCount = _document.PageCount;
            CurrentPageIndex = 0;
            _maxZoomForDocument = ComputeMaxZoomForDocument(_document);
            Zoom = Math.Min(Zoom, _maxZoomForDocument);

            await RenderAllPagesAsync(cancellationToken, isNewDocument: true).ConfigureAwait(false);
            StatusMessage = null;
            return OpenOutcome.Success;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Errore nell'apertura del documento: {ex.Message}";
            return OpenOutcome.Failed;
        }
    }

    [RelayCommand(CanExecute = nameof(CanGoToPreviousPage))]
    private void PreviousPage() => CurrentPageIndex--;

    private bool CanGoToPreviousPage() => CurrentPageIndex > 0;

    [RelayCommand(CanExecute = nameof(CanGoToNextPage))]
    private void NextPage() => CurrentPageIndex++;

    private bool CanGoToNextPage() => CurrentPageIndex < PageCount - 1;

    [RelayCommand(CanExecute = nameof(HasDocument))]
    private Task ZoomIn() => SetZoomAsync(Zoom * 1.25);

    [RelayCommand(CanExecute = nameof(HasDocument))]
    private Task ZoomOut() => SetZoomAsync(Zoom / 1.25);

    private bool HasDocument() => _document is not null;

    public Task SetZoomAsync(double zoom) => ApplyZoomAsync(Math.Clamp(zoom, MinZoom, _maxZoomForDocument));

    /// <summary>Sets zoom so a page of <paramref name="pageWidthPoints"/> fills <paramref name="viewportWidthPixels"/> (minus a small margin).</summary>
    public Task FitWidthAsync(double viewportWidthPixels, double pageWidthPoints)
    {
        if (pageWidthPoints <= 0 || viewportWidthPixels <= 0)
        {
            return Task.CompletedTask;
        }

        const double margin = 32; // leaves breathing room so the page border isn't flush against the scrollbar
        return ApplyZoomAsync(Math.Clamp((viewportWidthPixels - margin) / pageWidthPoints, MinZoom, _maxZoomForDocument));
    }

    /// <summary>Sets zoom so a full page fits within the viewport, both width and height.</summary>
    public Task FitPageAsync(double viewportWidthPixels, double viewportHeightPixels, double pageWidthPoints, double pageHeightPoints)
    {
        if (pageWidthPoints <= 0 || pageHeightPoints <= 0 || viewportWidthPixels <= 0 || viewportHeightPixels <= 0)
        {
            return Task.CompletedTask;
        }

        const double margin = 32;
        var zoomForWidth = (viewportWidthPixels - margin) / pageWidthPoints;
        var zoomForHeight = (viewportHeightPixels - margin) / pageHeightPoints;
        return ApplyZoomAsync(Math.Clamp(Math.Min(zoomForWidth, zoomForHeight), MinZoom, _maxZoomForDocument));
    }

    private async Task ApplyZoomAsync(double newZoom)
    {
        if (Math.Abs(newZoom - Zoom) < 0.001 || _document is null)
        {
            return;
        }

        Zoom = newZoom;
        OnPropertyChanged(nameof(ZoomPercent));
        OnPropertyChanged(nameof(ZoomPercentText));
        await RenderAllPagesAsync(CancellationToken.None, isNewDocument: false).ConfigureAwait(true);
    }

    public PdfDocument? Document => _document;

    /// <summary>
    /// Two-phase render: first size every page for the current zoom (cheap - no bitmap
    /// decode), so the scrollable area/scrollbar are correct immediately; then fill in
    /// bitmaps progressively in the background.
    /// </summary>
    /// <param name="isNewDocument">
    /// <c>true</c> when called right after opening a document (nothing on screen yet, so
    /// <see cref="Pages"/> is rebuilt from scratch with empty placeholders). <c>false</c> for
    /// a zoom-only refresh of the document already showing: the existing
    /// <see cref="PdfPageViewModel"/> instances are resized in place instead, so their
    /// *current* bitmap is immediately stretched/shrunk to the new size (a fast, pixelated
    /// "fake zoom") rather than the page going blank until the real re-render finishes.
    /// </param>
    /// <remarks>
    /// This still renders (and keeps in memory) every page of the document up front - true
    /// viewport virtualization (skip/unload far off-screen pages) is tracked in
    /// docs/ROADMAP.md M1 as a follow-up for very large documents.
    /// </remarks>
    private Task RenderAllPagesAsync(CancellationToken cancellationToken, bool isNewDocument)
    {
        // A zoom change (or a fast re-open) can be requested while the previous render is
        // still filling in bitmaps in the background. Chain behind it - rather than letting
        // both run at once - instead of just cancelling: PdfEngine now serializes native
        // calls internally (PdfiumRuntime.Gate), so an overlapping call would no longer
        // corrupt anything, but it would still (a) block the UI thread on that lock while
        // the resize/placeholder step below waits its turn, and (b) do wasted work rendering
        // pages for a request that is about to be superseded anyway.
        _renderCts?.Cancel();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _renderCts = cts;

        var previousRender = _renderTask;
        var renderTask = RunRenderAsync(previousRender, cts, isNewDocument);
        _renderTask = renderTask;
        return renderTask;
    }

    private async Task RunRenderAsync(Task previousRender, CancellationTokenSource cts, bool isNewDocument)
    {
        try
        {
            await previousRender.ConfigureAwait(true);
        }
        catch
        {
            // The previous render already surfaced its own errors/cancellation; this await
            // only exists to know when it is safe to touch the document again.
        }

        if (cts.IsCancellationRequested)
        {
            return;
        }

        var document = _document;
        if (document is null)
        {
            Pages.Clear();
            return;
        }

        var zoom = Zoom;
        List<PdfPageViewModel> placeholders;

        if (!isNewDocument && Pages.Count == document.PageCount)
        {
            // Zoom-only refresh of the document already on screen: resize every page in
            // place right now (cheap, no PDFium call - PointWidth/Height were captured when
            // the page was first created) so the layout - and whatever bitmap each page
            // already has, stretched via Stretch="Fill" - updates instantly, before the
            // background loop below replaces it with a crisp re-render.
            placeholders = Pages.ToList();
            foreach (var placeholder in placeholders)
            {
                placeholder.Resize(zoom);
            }
        }
        else
        {
            Pages.Clear();
            placeholders = new List<PdfPageViewModel>(document.PageCount);
            for (var index = 0; index < document.PageCount; index++)
            {
                using var page = document.GetPage(index);
                var (widthPt, heightPt) = page.SizeInPoints;
                var placeholder = new PdfPageViewModel(index, widthPt, heightPt, zoom);
                placeholders.Add(placeholder);
                Pages.Add(placeholder);
            }
        }

        try
        {
            foreach (var placeholder in placeholders)
            {
                cts.Token.ThrowIfCancellationRequested();

                var pageIndex = placeholder.Index;
                var pixelWidth = (int)Math.Round(placeholder.PixelWidth);
                var pixelHeight = (int)Math.Round(placeholder.PixelHeight);

                var bgra = await Task.Run(() =>
                {
                    using var page = document.GetPage(pageIndex);
                    return page.RenderToBgra(pixelWidth, pixelHeight);
                }, cts.Token).ConfigureAwait(true);

                var bitmap = new WriteableBitmap(pixelWidth, pixelHeight);
                using (var stream = bitmap.PixelBuffer.AsStream())
                {
                    stream.Write(bgra, 0, bgra.Length);
                }

                placeholder.Bitmap = bitmap;
            }
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer OpenAsync/zoom change - the placeholders/pages already
            // added belong to the previous request and get cleared at the start of the next one.
        }
        catch (Exception ex)
        {
            // A page failing to render (e.g. an out-of-memory allocation for an unexpectedly
            // huge page/zoom combination) must not crash the app - surface it and leave
            // whichever pages did render in place.
            StatusMessage = $"Errore durante il rendering: {ex.Message}";
        }

        PreviousPageCommand.NotifyCanExecuteChanged();
        NextPageCommand.NotifyCanExecuteChanged();
        ZoomInCommand.NotifyCanExecuteChanged();
        ZoomOutCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Caps zoom so that no page of <paramref name="document"/> can be rendered past
    /// <see cref="SafeMaxPixelDimension"/> on either axis - see the field's remarks.
    /// </summary>
    private static double ComputeMaxZoomForDocument(PdfDocument document)
    {
        double maxPointDimension = 1;
        for (var index = 0; index < document.PageCount; index++)
        {
            using var page = document.GetPage(index);
            var (widthPt, heightPt) = page.SizeInPoints;
            maxPointDimension = Math.Max(maxPointDimension, Math.Max(widthPt, heightPt));
        }

        return Math.Min(MaxZoom, SafeMaxPixelDimension / maxPointDimension);
    }

    partial void OnCurrentPageIndexChanged(int value)
    {
        PreviousPageCommand.NotifyCanExecuteChanged();
        NextPageCommand.NotifyCanExecuteChanged();
    }

    public void Dispose()
    {
        _renderCts?.Cancel();
        _document?.Dispose();
        _document = null;
    }
}
