using System.Runtime.InteropServices.WindowsRuntime;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Pdf26.Services;
using PdfEngine;

namespace Pdf26.ViewModels;

public partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly FileSourceResolver _sourceResolver = new();
    private PdfDocument? _document;

    [ObservableProperty]
    public partial WriteableBitmap? CurrentPageBitmap { get; set; }

    [ObservableProperty]
    public partial int CurrentPageIndex { get; set; }

    [ObservableProperty]
    public partial int PageCount { get; set; }

    [ObservableProperty]
    public partial string? DocumentTitle { get; set; }

    [ObservableProperty]
    public partial string? StatusMessage { get; set; }

    /// <summary>Render zoom: pixels-per-PDF-point. 96/72 ≈ 1.33 matches typical screen DPI at 100% zoom.</summary>
    public double Zoom { get; set; } = 96.0 / 72.0;

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
            StatusMessage = null;
            RenderCurrentPage();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Errore nell'apertura del documento: {ex.Message}";
        }
    }

    [RelayCommand(CanExecute = nameof(CanGoToPreviousPage))]
    private void PreviousPage()
    {
        CurrentPageIndex--;
        RenderCurrentPage();
    }

    private bool CanGoToPreviousPage() => _document is not null && CurrentPageIndex > 0;

    [RelayCommand(CanExecute = nameof(CanGoToNextPage))]
    private void NextPage()
    {
        CurrentPageIndex++;
        RenderCurrentPage();
    }

    private bool CanGoToNextPage() => _document is not null && CurrentPageIndex < PageCount - 1;

    public PdfDocument? Document => _document;

    private void RenderCurrentPage()
    {
        if (_document is null || PageCount == 0)
        {
            CurrentPageBitmap = null;
            return;
        }

        using var page = _document.GetPage(CurrentPageIndex);
        var (widthPt, heightPt) = page.SizeInPoints;
        var pixelWidth = Math.Max(1, (int)Math.Round(widthPt * Zoom));
        var pixelHeight = Math.Max(1, (int)Math.Round(heightPt * Zoom));

        var bgra = page.RenderToBgra(pixelWidth, pixelHeight);

        var bitmap = new WriteableBitmap(pixelWidth, pixelHeight);
        using (var stream = bitmap.PixelBuffer.AsStream())
        {
            stream.Write(bgra, 0, bgra.Length);
        }

        CurrentPageBitmap = bitmap;
        PreviousPageCommand.NotifyCanExecuteChanged();
        NextPageCommand.NotifyCanExecuteChanged();
    }

    public void Dispose()
    {
        _document?.Dispose();
        _document = null;
    }
}
