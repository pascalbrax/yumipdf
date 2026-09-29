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

    /// <summary>How long after the window itself scrolls the document to a page (an animated scroll) the scroll position is not read back as "the user moved to another page".</summary>
    private const long ProgrammaticScrollGraceMilliseconds = 800;

    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _refitTimer;
    private long _ignoreScrollSyncUntil;
    private bool _syncingFromScroll;

    public MainViewModel ViewModel { get; } = new();

    public MainWindow(PdfSource? initialSource)
    {
        _initialSource = initialSource;
        InitializeComponent();
        Title = "PDF26";
        WindowIcon.Apply(this);

        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        PagesScrollViewer.ViewChanged += OnPagesViewChanged;
        PagesScrollViewer.SizeChanged += OnPagesViewportSizeChanged;
        ThumbnailStrip.PageActivated += OnThumbnailPageActivated;
        ThumbnailStrip.CenterPageChanged += (_, pageIndex) => ViewModel.SetThumbnailFocus(pageIndex);
        UpdateThumbnailHandleGlyph();

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
            // The document is already where it should be when the change came from scrolling it.
            if (!_syncingFromScroll)
            {
                ScrollToPage(ViewModel.CurrentPageIndex);
            }

            ThumbnailStrip.ScrollToPage(ViewModel.CurrentPageIndex);
        }
        else if (e.PropertyName == nameof(MainViewModel.Thumbnails))
        {
            ThumbnailStrip.SetSource(ViewModel.Thumbnails);
            ThumbnailStrip.ScrollToPage(ViewModel.CurrentPageIndex, animate: false);
        }
        else if (e.PropertyName == nameof(MainViewModel.IsThumbnailPaneOpen))
        {
            UpdateThumbnailHandleGlyph();
        }
        else if (e.PropertyName == nameof(MainViewModel.IsSearchOpen) && ViewModel.IsSearchOpen)
        {
            // Ctrl+F (or the toolbar toggle) should let the user start typing immediately,
            // not just show an empty bar they then have to click into.
            SearchBox.Focus(FocusState.Programmatic);
            SearchBox.SelectAll();
        }
    }

    private void OnSearchBoxKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case Windows.System.VirtualKey.Enter:
                // Shift+Enter searches backwards, matching the usual browser Find convention.
                if (ViewModel.FindPreviousCommand.CanExecute(null)
                    && (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0)
                {
                    ViewModel.FindPreviousCommand.Execute(null);
                }
                else if (ViewModel.FindNextCommand.CanExecute(null))
                {
                    ViewModel.FindNextCommand.Execute(null);
                }

                e.Handled = true;
                break;

            case Windows.System.VirtualKey.Escape:
                ViewModel.IsSearchOpen = false;
                e.Handled = true;
                break;
        }
    }

    private void OnCloseSearchClicked(object sender, RoutedEventArgs e) => ViewModel.IsSearchOpen = false;

    private void ScrollToPage(int index)
    {
        if (PagesItemsControl.ContainerFromIndex(index) is FrameworkElement container)
        {
            _ignoreScrollSyncUntil = Environment.TickCount64 + ProgrammaticScrollGraceMilliseconds;
            container.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = true });
        }
    }

    /// <summary>
    /// Keeps <see cref="MainViewModel.CurrentPageIndex"/> (and through it the thumbnail strip's
    /// centered page, and the Precedente/Successiva buttons) in step with what the user is
    /// actually looking at when they scroll the document themselves. Skipped while this window's
    /// own animated scroll is still running, whose intermediate positions are not the user's choice.
    /// </summary>
    private void OnPagesViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (Environment.TickCount64 < _ignoreScrollSyncUntil || ViewModel.Pages.Count == 0)
        {
            return;
        }

        var page = PageAtViewportCenter();
        if (page < 0 || page == ViewModel.CurrentPageIndex)
        {
            return;
        }

        _syncingFromScroll = true;
        try
        {
            ViewModel.CurrentPageIndex = page;
        }
        finally
        {
            _syncingFromScroll = false;
        }
    }

    /// <summary>The page under the middle of the viewport, worked out from the page sizes (pages are laid out top to bottom, 24 px of padding around them and 16 px between them).</summary>
    private int PageAtViewportCenter()
    {
        const double padding = 24;
        const double spacing = 16;

        var centerLine = PagesScrollViewer.VerticalOffset + PagesScrollViewer.ViewportHeight / 2;
        var top = padding;
        var pages = ViewModel.Pages;
        for (var index = 0; index < pages.Count; index++)
        {
            var bottom = top + pages[index].PixelHeight;
            if (centerLine <= bottom + spacing / 2)
            {
                return index;
            }

            top = bottom + spacing;
        }

        return pages.Count - 1;
    }

    private void OnThumbnailPageActivated(object? sender, int pageIndex)
    {
        if (ViewModel.CurrentPageIndex == pageIndex)
        {
            // No change to react to, but the user still asked to see this page.
            ScrollToPage(pageIndex);
        }
        else
        {
            ViewModel.CurrentPageIndex = pageIndex;
        }
    }

    private void OnThumbnailHandleClicked(object sender, RoutedEventArgs e)
        => ViewModel.IsThumbnailPanelOpen = !ViewModel.IsThumbnailPanelOpen;

    private void UpdateThumbnailHandleGlyph()
    {
        // Points at where the strip will go: back to the left edge when it is open, out to the right when closed.
        ThumbnailHandleGlyph.Glyph = ViewModel.IsThumbnailPaneOpen ? "\uE76B" : "\uE76C";
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

    private async void OnFitWidthClicked(object sender, RoutedEventArgs e) => await FitWidthToViewportAsync();

    /// <summary>Fits the widest page to the room the pages currently have - see <see cref="MainViewModel.FitWidthAsync"/> for why the widest.</summary>
    private Task FitWidthToViewportAsync()
    {
        if (ViewModel.Pages.Count == 0)
        {
            return Task.CompletedTask;
        }

        var widestPagePoints = ViewModel.Pages.Max(page => page.PointWidth);
        return ViewModel.FitWidthAsync(PagesScrollViewer.ViewportWidth, widestPagePoints);
    }

    /// <summary>
    /// While "fit width" is on, follows the pages' room changing (thumbnail strip or form panel
    /// opening/closing, window resize). Debounced: a pane animation or a window drag fires this
    /// many times, and only where it settles matters.
    /// </summary>
    private void OnPagesViewportSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!ViewModel.IsFitWidthActive || Math.Abs(e.NewSize.Width - e.PreviousSize.Width) < 0.5)
        {
            return;
        }

        if (_refitTimer is null)
        {
            _refitTimer = DispatcherQueue.CreateTimer();
            _refitTimer.Interval = TimeSpan.FromMilliseconds(150);
            _refitTimer.IsRepeating = false;
            _refitTimer.Tick += async (_, _) =>
            {
                if (ViewModel.IsFitWidthActive)
                {
                    await FitWidthToViewportAsync();
                }
            };
        }

        _refitTimer.Stop();
        _refitTimer.Start();
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
