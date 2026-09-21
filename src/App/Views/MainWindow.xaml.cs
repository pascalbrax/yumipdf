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
            _ = OpenSourceAsync(_initialSource);
        }
    }

    /// <summary>Opens a source, prompting for a password (and re-prompting on a wrong one) as many times as needed.</summary>
    private async Task OpenSourceAsync(PdfSource source)
    {
        string? password = null;
        while (true)
        {
            var outcome = await ViewModel.OpenAsync(source, password);
            if (outcome != OpenOutcome.PasswordRequired)
            {
                return;
            }

            var dialog = new PasswordPromptDialog(previousAttemptFailed: password is not null)
            {
                XamlRoot = Content.XamlRoot,
            };

            var result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary)
            {
                ViewModel.StatusMessage = "Apertura annullata: documento protetto da password.";
                return;
            }

            password = dialog.Password;
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
            await OpenSourceAsync(CommandLineParser.Classify(file.Path));
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
                WindowNative.GetWindowHandle(this), ViewModel.Document, ViewModel.DocumentTitle ?? "PDF26",
                ViewModel.RenderPageForPrint);
        }
        catch (Exception ex)
        {
            ViewModel.StatusMessage = $"Errore di stampa: {ex.Message}";
        }
    }

    private async void OnFitWidthClicked(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Pages.Count == 0)
        {
            return;
        }

        var firstPage = ViewModel.Pages[0];
        var pageWidthPoints = firstPage.PixelWidth / ViewModel.Zoom;
        await ViewModel.FitWidthAsync(PagesScrollViewer.ViewportWidth, pageWidthPoints);
    }

    private async void OnFitPageClicked(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Pages.Count == 0)
        {
            return;
        }

        var firstPage = ViewModel.Pages[0];
        var pageWidthPoints = firstPage.PixelWidth / ViewModel.Zoom;
        var pageHeightPoints = firstPage.PixelHeight / ViewModel.Zoom;
        await ViewModel.FitPageAsync(PagesScrollViewer.ViewportWidth, PagesScrollViewer.ViewportHeight, pageWidthPoints, pageHeightPoints);
    }

    private async void OnSettingsClicked(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsDialog(ViewModel.PageAreaBackground.Color, FileAssociationService.IsRegistered())
        {
            XamlRoot = Content.XamlRoot,
        };

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary)
        {
            return;
        }

        ViewModel.UpdateBackgroundColor(dialog.SelectedColor);

        try
        {
            if (dialog.AssociatePdf)
            {
                FileAssociationService.Register();
            }
            else
            {
                FileAssociationService.Unregister();
            }
        }
        catch (Exception ex)
        {
            ViewModel.StatusMessage = $"Impossibile aggiornare l'associazione dei file .pdf: {ex.Message}";
        }
    }

    private async void OnSaveFilledFormClicked(object sender, RoutedEventArgs e)
    {
        var picker = new FileSavePicker();
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));

        picker.FileTypeChoices.Add("Documento PDF", new List<string> { ".pdf" });
        picker.SuggestedFileName = ViewModel.DocumentTitle is { } title
            ? $"{Path.GetFileNameWithoutExtension(title)}-compilato"
            : "documento-compilato";

        var file = await picker.PickSaveFileAsync();
        if (file is null)
        {
            return;
        }

        var flatten = ViewModel.FlattenBeforeSave;
        var saved = await ViewModel.SaveFilledFormAsync(file.Path, flatten);
        // When flatten is true, SaveFilledFormAsync already sets its own status message
        // (the panel/filler get torn down at the same time) - do not overwrite it here.
        if (saved && !flatten)
        {
            ViewModel.InfoMessage = $"Salvato in {file.Path}";
        }
    }
}
