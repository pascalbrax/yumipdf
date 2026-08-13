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
    private readonly FileSourceResolver _sourceResolver = new();
    private readonly SettingsService _settingsService = new();
    private PdfDocument? _document;
    private CancellationTokenSource? _renderCts;

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

    /// <summary>Render zoom: pixels-per-PDF-point. 96/72 ≈ 1.33 matches typical screen DPI at 100% zoom.</summary>
    public double Zoom { get; set; } = 96.0 / 72.0;

    public MainViewModel()
    {
        PageAreaBackground = new SolidColorBrush(ColorHex.Parse(_settingsService.Load().PageAreaBackgroundHex));
    }

    public void UpdateBackgroundColor(Color color)
    {
        PageAreaBackground = new SolidColorBrush(color);
        _settingsService.Save(new AppSettings { PageAreaBackgroundHex = ColorHex.ToHex(color) });
    }

    public async Task OpenAsync(PdfSource source, CancellationToken cancellationToken = default)
    {
        StatusMessage = $"Apertura di {source.Value}...";
        try
        {
            var localPath = await _sourceResolver.ResolveToLocalPathAsync(source, cancellationToken).ConfigureAwait(false);

            _document?.Dispose();
            _document = PdfDocument.OpenFromPath(localPath);

            DocumentTitle = Path.GetFileName(localPath);
            PageCount = _document.PageCount;
            CurrentPageIndex = 0;

            await RenderAllPagesAsync(cancellationToken).ConfigureAwait(false);
            StatusMessage = null;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Errore nell'apertura del documento: {ex.Message}";
        }
    }

    [RelayCommand(CanExecute = nameof(CanGoToPreviousPage))]
    private void PreviousPage() => CurrentPageIndex--;

    private bool CanGoToPreviousPage() => CurrentPageIndex > 0;

    [RelayCommand(CanExecute = nameof(CanGoToNextPage))]
    private void NextPage() => CurrentPageIndex++;

    private bool CanGoToNextPage() => CurrentPageIndex < PageCount - 1;

    public PdfDocument? Document => _document;

    /// <summary>
    /// Renders every page up front so the view can lay them out in one continuous,
    /// scrollable column (no more forced page-by-page navigation). PDFium rendering runs on
    /// a background thread (pages are opened/closed serially, one at a time, so this does
    /// not race the UI thread's use of <see cref="_document"/>); only WriteableBitmap
    /// creation - which must happen on the UI thread - runs inline per page.
    /// </summary>
    /// <remarks>
    /// For very large documents this eagerly renders (and keeps in memory) every page - see
    /// docs/ROADMAP.md M1 for the planned windowed/virtualized rendering follow-up.
    /// </remarks>
    private async Task RenderAllPagesAsync(CancellationToken cancellationToken)
    {
        _renderCts?.Cancel();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _renderCts = cts;

        Pages.Clear();
        var document = _document;
        if (document is null)
        {
            return;
        }

        for (var index = 0; index < document.PageCount; index++)
        {
            cts.Token.ThrowIfCancellationRequested();

            var pageIndex = index;
            var (bgra, pixelWidth, pixelHeight) = await Task.Run(() =>
            {
                using var page = document.GetPage(pageIndex);
                var (widthPt, heightPt) = page.SizeInPoints;
                var w = Math.Max(1, (int)Math.Round(widthPt * Zoom));
                var h = Math.Max(1, (int)Math.Round(heightPt * Zoom));
                return (page.RenderToBgra(w, h), w, h);
            }, cts.Token).ConfigureAwait(true);

            var bitmap = new WriteableBitmap(pixelWidth, pixelHeight);
            using (var stream = bitmap.PixelBuffer.AsStream())
            {
                stream.Write(bgra, 0, bgra.Length);
            }

            Pages.Add(new PdfPageViewModel(pageIndex, bitmap));
        }

        PreviousPageCommand.NotifyCanExecuteChanged();
        NextPageCommand.NotifyCanExecuteChanged();
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
