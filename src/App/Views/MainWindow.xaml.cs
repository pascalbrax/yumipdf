using Microsoft.UI.Xaml;
using Windows.Storage.Pickers;
using WinRT.Interop;
using App26Pdf.Services;
using App26Pdf.ViewModels;
using Printing;

namespace App26Pdf.Views;

public sealed partial class MainWindow : Window
{
    private readonly PrintService _printService = new();
    private readonly PdfSource? _initialSource;

    public MainViewModel ViewModel { get; } = new();

    public MainWindow(PdfSource? initialSource)
    {
        _initialSource = initialSource;
        InitializeComponent();
        Title = "26PDF";

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
                WindowNative.GetWindowHandle(this), ViewModel.Document, ViewModel.DocumentTitle ?? "26PDF");
        }
        catch (Exception ex)
        {
            ViewModel.StatusMessage = $"Errore di stampa: {ex.Message}";
        }
    }
}
