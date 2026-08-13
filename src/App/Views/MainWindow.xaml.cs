using Microsoft.UI.Xaml;
using Windows.Storage.Pickers;
using WinRT.Interop;
using Pdf26.Services;
using Pdf26.ViewModels;
using Printing;

namespace Pdf26.Views;

public sealed partial class MainWindow : Window
{
    private readonly PrintService _printService = new();
    private readonly PdfSource? _initialSource;

    public MainViewModel ViewModel { get; } = new();

    public MainWindow(PdfSource? initialSource)
    {
        _initialSource = initialSource;
        InitializeComponent();
        Title = "PDF26";

        if (_initialSource is not null)
        {
            _ = ViewModel.OpenAsync(_initialSource);
        }
    }

    private async void OnOpenClicked(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        // Unpackaged Win32 apps must associate WinRT pickers with a window handle explicitly.
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));

        picker.FileTypeFilter.Add(".pdf");
        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;

        var file = await picker.PickSingleFileAsync();
        if (file is not null)
        {
            await ViewModel.OpenAsync(CommandLineParser.Classify(file.Path));
        }
    }

    private void OnPrintClicked(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Document is null)
        {
            return;
        }

        try
        {
            _printService.TryShowPrintDialogAndPrint(
                WindowNative.GetWindowHandle(this), ViewModel.Document, ViewModel.DocumentTitle ?? "PDF26");
        }
        catch (Exception ex)
        {
            ViewModel.StatusMessage = $"Errore di stampa: {ex.Message}";
        }
    }
}
