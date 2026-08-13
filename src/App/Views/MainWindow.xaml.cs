using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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

        ViewModel.PropertyChanged += OnViewModelPropertyChanged;

        if (_initialSource is not null)
        {
            _ = ViewModel.OpenAsync(_initialSource);
        }
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // The Precedente/Successiva buttons just move CurrentPageIndex; scrolling the
        // already-continuous page list into position happens here.
        if (e.PropertyName == nameof(MainViewModel.CurrentPageIndex))
        {
            ScrollToPage(ViewModel.CurrentPageIndex);
        }
    }

    private void ScrollToPage(int index)
    {
        if (PagesItemsControl.ContainerFromIndex(index) is FrameworkElement container)
        {
            container.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = true });
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

    private async void OnSettingsClicked(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsDialog(ViewModel.PageAreaBackground.Color)
        {
            XamlRoot = Content.XamlRoot,
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            ViewModel.UpdateBackgroundColor(dialog.SelectedColor);
        }
    }
}
