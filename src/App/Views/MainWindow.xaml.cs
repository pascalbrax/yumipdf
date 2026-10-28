using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics.Imaging;
using PdfEngine.Viewing;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;
using Pdf26.Services;
using Pdf26.ViewModels;

namespace Pdf26.Views;

public sealed partial class MainWindow : Window
{
    private readonly NativePrintService _printService;
    private bool _preparingPrint;
    private readonly PdfSource? _initialSource;

    /// <summary>How long after the window itself scrolls the document to a page (an animated scroll) the scroll position is not read back as "the user moved to another page".</summary>
    private const long ProgrammaticScrollGraceMilliseconds = 800;

    /// <summary>Layout of the page list (see MainWindow.xaml): space around the pages and between them, in pixels.</summary>
    private const double PagePadding = 24;
    private const double PageSpacing = 16;

    /// <summary>How long after a zoom change the scroll position is not read back as the user's.</summary>
    private const long ZoomSettleMilliseconds = 500;

    /// <summary>Wait after the last scroll/resize event before telling the view model which pages are on screen (see <see cref="ScheduleViewedPagesUpdate"/>).</summary>
    private const int ViewedPagesDebounceMilliseconds = 80;

    /// <summary>How often the memory figure in the status bar is read.</summary>
    private const int MemoryRefreshSeconds = 2;

    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _refitTimer;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _viewedPagesTimer;
    private long _ignoreScrollSyncUntil;
    private bool _syncingFromScroll;
    private (int Page, double Fraction)? _zoomAnchor;
    private bool _closeConfirmed;

    public MainViewModel ViewModel { get; } = new();

    public MainWindow(PdfSource? initialSource)
    {
        _initialSource = initialSource;
        InitializeComponent();
        _printService = new NativePrintService(this);
        _printService.StateChanged += () => PrintButton.IsEnabled = !_preparingPrint && !_printService.IsBusy;
        _printService.Failed += message => ViewModel.StatusMessage = message;
        _printService.Completed += message => ViewModel.InfoMessage = message;
        Closed += (_, _) => _printService.Dispose();
        StartMemoryUsageTimer();
        Title = "YumiPDF";
        WindowIcon.Apply(this);

        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        ViewModel.PagesResizing += OnPagesResizing;
        ViewModel.PagesResized += OnPagesResized;
        PagesScrollViewer.ViewChanged += OnPagesViewChanged;
        PagesScrollViewer.SizeChanged += OnPagesViewportSizeChanged;
        PagesItemsControl.SizeChanged += (_, _) => ScheduleViewedPagesUpdate();
        ThumbnailStrip.PageActivated += OnThumbnailPageActivated;
        ThumbnailStrip.CenterPageChanged += (_, pageIndex) => ViewModel.SetThumbnailFocus(pageIndex);
        UpdateSidebarHandleGlyph();
        // A click anywhere on the pages that is not on an annotation clears the selection. Heard even when
        // something underneath handled it (a text selection drag, a form field): those still count as "elsewhere".
        PagesScrollViewer.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnPagesPointerPressed), handledEventsToo: true);
        PagesScrollViewer.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler(OnPagesPointerWheelChanged), handledEventsToo: true);
        AppWindow.Closing += OnAppWindowClosing;

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
        // Buttons that appear or disappear (a document opened, a panel shown) change how much of the toolbar fits.
        if (e.PropertyName is nameof(MainViewModel.DocumentToolsVisibility) or nameof(MainViewModel.ThumbnailButtonVisibility)
            or nameof(MainViewModel.BookmarkButtonVisibility) or nameof(MainViewModel.FormButtonVisibility)
            or nameof(MainViewModel.IsToolsPaneOpen))
        {
            RefreshToolbarOverflow();
        }

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
        else if (e.PropertyName == nameof(MainViewModel.CurrentBookmark))
        {
            RevealCurrentBookmark();
        }
        else if (e.PropertyName == nameof(MainViewModel.IsToolsPaneOpen) && ViewModel.IsToolsPaneOpen)
        {
            RefreshUndoRedoAppearance();
        }
        else if (e.PropertyName == nameof(MainViewModel.Thumbnails))
        {
            ThumbnailStrip.SetSource(ViewModel.Thumbnails);
            ThumbnailStrip.ScrollToPage(ViewModel.CurrentPageIndex, animate: false);
        }
        else if (e.PropertyName == nameof(MainViewModel.IsLeftPaneOpen))
        {
            UpdateSidebarHandleGlyph();
        }
        else if (e.PropertyName == nameof(MainViewModel.IsSearchOpen) && ViewModel.IsSearchOpen)
        {
            // Ctrl+F (or the toolbar toggle) should let the user start typing immediately,
            // not just show an empty bar they then have to click into. Deferred a tick: the
            // bar's Visibility flips to Visible on this same property-changed event (x:Bind's
            // own subscriber runs first, registered earlier in InitializeComponent), but an
            // element that was Collapsed a moment ago has not been through a layout pass yet,
            // and Focus() on it can silently fail before that happens.
            DispatcherQueue.TryEnqueue(() =>
            {
                SearchBox.Focus(FocusState.Programmatic);
                SearchBox.SelectAll();
            });
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
            // Top-aligned: "go to page N" (index, thumbnails, Precedente/Successiva) should put
            // the page at the top of the view. The default only scrolls as far as needed to make
            // it visible, which does nothing at all for a page that is already on screen.
            container.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = true, VerticalAlignmentRatio = 0.0 });
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
        UpdatePageRowMinWidth();
        ScheduleViewedPagesUpdate();
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

    /// <summary>
    /// A zoom change resizes every page, but the scroll viewer keeps its offset in pixels: without this the
    /// document slides under the user (page 30 at 125% shows page 24 or so), and the scroll handler then
    /// reads that wrong position as the current page. So: note the page at the top of the view and how far
    /// into it the view starts (a fraction, which survives the resize)...
    /// </summary>
    private void OnPagesResizing()
    {
        // The offset is about to move because of the resize, not because the user scrolled.
        _ignoreScrollSyncUntil = Environment.TickCount64 + ZoomSettleMilliseconds;

        var pages = ViewModel.Pages;
        if (pages.Count == 0)
        {
            _zoomAnchor = null;
            return;
        }

        var offset = PagesScrollViewer.VerticalOffset;
        var top = PagePadding;
        for (var index = 0; index < pages.Count; index++)
        {
            var height = pages[index].PixelHeight;
            if (offset <= top + height + PageSpacing / 2 || index == pages.Count - 1)
            {
                _zoomAnchor = (index, height > 0 ? (offset - top) / height : 0);
                return;
            }

            top += height + PageSpacing;
        }
    }

    /// <summary>...and once the pages have their new size, scroll to the same page at the same fraction.</summary>
    private void OnPagesResized()
    {
        if (_zoomAnchor is not var (page, fraction) || page >= ViewModel.Pages.Count)
        {
            PublishViewedPages();
            return;
        }

        _zoomAnchor = null;
        var top = PagePadding;
        for (var index = 0; index < page; index++)
        {
            top += ViewModel.Pages[index].PixelHeight + PageSpacing;
        }

        var target = Math.Max(0, top + fraction * ViewModel.Pages[page].PixelHeight);

        // The new extent only exists after a layout pass; before it the scroll viewer would clamp the offset to the old, shorter one.
        _ignoreScrollSyncUntil = Environment.TickCount64 + ZoomSettleMilliseconds;
        PagesScrollViewer.UpdateLayout();
        PagesScrollViewer.ChangeView(null, target, null, disableAnimation: true);

        // The pages shown after a zoom are known from the target offset - do not wait for the scroll viewer to report it.
        PublishViewedPages(target);
    }

    /// <summary>
    /// Every row of the page list is at least as wide as the room the window leaves (its width less the 24 px of padding on each
    /// side, see MainWindow.xaml), which is what centers a narrow page in the window - the vertical scroll bar appearing changes
    /// that room without the scroll viewer changing size, hence the call from the scroll handler too.
    /// </summary>
    private void UpdatePageRowMinWidth()
    {
        var width = Math.Max(0, Math.Floor(PagesScrollViewer.ViewportWidth - 48));
        if (Math.Abs(width - ViewModel.PageRowMinWidth) >= 1)
        {
            ViewModel.PageRowMinWidth = width;
        }
    }

    /// <summary>
    /// The toolbar moves the buttons that do not fit into the "..." menu, but it only works that out when its own size changes:
    /// buttons shown after the window opened (the ones that need a document) were left clipped at the right edge, with no "..."
    /// to reach them, until the window was resized. Narrowing the bar by one pixel for one layout pass, then letting it fill the
    /// window again, is the same size change the user's resizing makes - the only thing that was found to make it measure again
    /// (switching <c>IsDynamicOverflowEnabled</c> off and on, or invalidating its measure, did not).
    /// </summary>
    private void RefreshToolbarOverflow()
        => DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            MainCommandBar.UpdateLayout();
            var width = MainCommandBar.ActualWidth;
            if (width < 2)
            {
                return;
            }

            MainCommandBar.Width = width - 1;
            MainCommandBar.UpdateLayout();
            MainCommandBar.ClearValue(FrameworkElement.WidthProperty);
            MainCommandBar.UpdateLayout();
        });

    /// <summary>Keeps the memory figure at the right of the status bar current: read now and then every <see cref="MemoryRefreshSeconds"/> seconds.</summary>
    private void StartMemoryUsageTimer()
    {
        ViewModel.RefreshMemoryUsage();
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(MemoryRefreshSeconds);
        timer.IsRepeating = true;
        timer.Tick += (_, _) => ViewModel.RefreshMemoryUsage();
        timer.Start();
        Closed += (_, _) => timer.Stop();
    }

    /// <summary>
    /// Tells the view model which pages the viewport shows, once scrolling or resizing has settled for a moment: it keeps
    /// those pages (and the ones around the current page) rendered and releases the rest. Not while the window is scrolling
    /// itself to a page (an animated jump through the pages in between): the pages passed over are not worth rendering.
    /// </summary>
    private void ScheduleViewedPagesUpdate()
    {
        if (_viewedPagesTimer is null)
        {
            _viewedPagesTimer = DispatcherQueue.CreateTimer();
            _viewedPagesTimer.Interval = TimeSpan.FromMilliseconds(ViewedPagesDebounceMilliseconds);
            _viewedPagesTimer.IsRepeating = false;
            _viewedPagesTimer.Tick += (_, _) =>
            {
                if (Environment.TickCount64 < _ignoreScrollSyncUntil)
                {
                    _viewedPagesTimer.Start();
                    return;
                }

                PublishViewedPages();
            };
        }

        _viewedPagesTimer.Stop();
        _viewedPagesTimer.Start();
    }

    private void PublishViewedPages(double? offset = null)
    {
        var pages = ViewModel.Pages;
        var (first, last) = PageWindow.VisiblePages(
            pages.Count, index => pages[index].PixelHeight, PagePadding, PageSpacing,
            offset ?? PagesScrollViewer.VerticalOffset, PagesScrollViewer.ViewportHeight);
        ViewModel.SetViewedPages(first, last);
    }

    /// <summary>The page under the middle of the viewport, worked out from the page sizes (pages are laid out top to bottom, 24 px of padding around them and 16 px between them).</summary>
    private int PageAtViewportCenter()
    {
        const double padding = PagePadding;
        const double spacing = PageSpacing;

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

    private void OnThumbnailPageActivated(object? sender, int pageIndex) => GoToPage(pageIndex);

    /// <summary>Scrolls the index so the highlighted entry is on screen - it follows the page being read.</summary>
    private void RevealCurrentBookmark()
    {
        if (ViewModel.CurrentBookmark is not { } entry || !ViewModel.IsBookmarkPaneOpen)
        {
            return;
        }

        // Low priority: its ancestors may have just been expanded, and the item only exists once that has been laid out.
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            var node = FindBookmarkNode(BookmarkTree.RootNodes, entry);
            if (node is not null && BookmarkTree.ContainerFromNode(node) is TreeViewItem container)
            {
                container.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = true });
            }
        });
    }

    private static TreeViewNode? FindBookmarkNode(IList<TreeViewNode> nodes, BookmarkNodeViewModel entry)
    {
        foreach (var node in nodes)
        {
            if (ReferenceEquals(node.Content, entry))
            {
                return node;
            }

            if (FindBookmarkNode(node.Children, entry) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private void OnBookmarkInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
    {
        if (args.InvokedItem is BookmarkNodeViewModel { CanNavigate: true } node)
        {
            GoToPage(node.PageIndex);
        }
    }

    private void GoToPage(int pageIndex)
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

    /// <summary>Whether the handle should bring back the index (rather than the thumbnails) the next time it opens the sidebar - whichever it closed last.</summary>
    private bool _handleReopensBookmarks;

    private void OnSidebarHandleClicked(object sender, RoutedEventArgs e)
    {
        if (ViewModel.IsLeftPaneOpen)
        {
            // Closes whichever panel is open; of thumbnails and index, the one that was showing is what the next click brings back.
            if (ViewModel.IsBookmarkPaneOpen)
            {
                _handleReopensBookmarks = true;
            }
            else if (ViewModel.IsThumbnailPaneOpen)
            {
                _handleReopensBookmarks = false;
            }

            ViewModel.CloseSidebar();
        }
        else if ((_handleReopensBookmarks || ViewModel.Thumbnails is null) && ViewModel.Bookmarks.Count > 0)
        {
            ViewModel.IsBookmarkPanelOpen = true;
        }
        else if (ViewModel.Thumbnails is not null)
        {
            ViewModel.IsThumbnailPanelOpen = true;
        }
    }

    private void UpdateSidebarHandleGlyph()
    {
        // Points at where the sidebar will go: back to the left edge when it shows thumbnails or the index, out to the right when closed.
        SidebarHandleGlyph.Glyph = ViewModel.IsLeftPaneOpen ? "" : "";
    }

    // ---- recent files ------------------------------------------------------------------

    /// <summary>Opens a recent document (after the unsaved-changes check) and closes the panel: the user moved on.</summary>
    private async void OnRecentEntryClicked(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not RecentFileEntry entry || !await ConfirmLeavingDocumentAsync())
        {
            return;
        }

        ViewModel.IsRecentPanelOpen = false;
        await OpenSourceAsync(CommandLineParser.Classify(entry.Path));
    }

    private void OnRemoveRecentClicked(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is RecentFileEntry entry)
        {
            ViewModel.RemoveRecent(entry.Path);
        }
    }

    private void OnToggleMoreStampsClicked(object sender, RoutedEventArgs e)
        => ViewModel.AreMoreStampsExpanded = !ViewModel.AreMoreStampsExpanded;

    private void OnClearRecentClicked(object sender, RoutedEventArgs e) => ViewModel.ClearRecent();

    private async void OnOpenClicked(object sender, RoutedEventArgs e)
    {
        if (!await ConfirmLeavingDocumentAsync())
        {
            return;
        }

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

    private async void OnPrintClicked(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Document is null || _preparingPrint || _printService.IsBusy)
        {
            return;
        }

        try
        {
            _preparingPrint = true;
            PrintButton.IsEnabled = false;
            ViewModel.StatusMessage = null;
            var title = ViewModel.DocumentTitle ?? "YumiPDF";
            var snapshot = await ViewModel.CreatePrintSnapshotAsync();
            await _printService.ShowAsync(snapshot, title);
        }
        catch (Exception ex)
        {
            ViewModel.StatusMessage = $"Errore di stampa: {ex.Message}";
        }
        finally
        {
            _preparingPrint = false;
            PrintButton.IsEnabled = !_printService.IsBusy;
        }
    }

    private async void OnFitWidthClicked(object sender, RoutedEventArgs e) => await FitWidthToViewportAsync();

    /// <summary>
    /// Fits the width the "pagine di dimensioni diverse" option asks for (<see cref="MainViewModel.FitReferenceWidth"/>): the page
    /// the user is actually looking at - not some other page; a document can mix page sizes/orientations (a landscape page
    /// among portrait ones), and fitting to any page but the current one leaves that one wrong (too small, or cropped) - or the
    /// width most pages have. Deliberately does not react to
    /// <see cref="MainViewModel.CurrentPageIndex"/> changing on its own - the user confirmed
    /// the fit should stay put when browsing to a different page, not silently re-zoom under
    /// them; only the room available (<see cref="OnPagesViewportSizeChanged"/>) re-triggers it,
    /// which still means "the same current page, now shown in a different amount of room".
    /// </summary>
    private Task FitWidthToViewportAsync()
    {
        if (ViewModel.Pages.Count == 0)
        {
            return Task.CompletedTask;
        }

        return ViewModel.FitWidthAsync(PagesScrollViewer.ViewportWidth, ViewModel.FitReferenceWidth(ViewModel.CurrentPageIndex));
    }

    /// <summary>
    /// While "fit width" is on, follows the pages' room changing (thumbnail strip or form panel
    /// opening/closing, window resize). Debounced: a pane animation or a window drag fires this
    /// many times, and only where it settles matters.
    /// </summary>
    private void OnPagesViewportSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdatePageRowMinWidth();
        ScheduleViewedPagesUpdate();
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

    /// <summary>Fits the page the user is actually looking at - see <see cref="FitWidthToViewportAsync"/>'s remarks (the same reasoning applies here, minus the room-change re-trigger: "Adatta a pagina" was already a one-shot action, not a persistent mode).</summary>
    private async void OnFitPageClicked(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Pages.Count == 0)
        {
            return;
        }

        var currentPage = ViewModel.Pages[Math.Clamp(ViewModel.CurrentPageIndex, 0, ViewModel.Pages.Count - 1)];
        var pageWidthPoints = currentPage.PixelWidth / ViewModel.Zoom;
        var pageHeightPoints = currentPage.PixelHeight / ViewModel.Zoom;
        await ViewModel.FitPageAsync(PagesScrollViewer.ViewportWidth, PagesScrollViewer.ViewportHeight, pageWidthPoints, pageHeightPoints);
    }

    private async void OnSettingsClicked(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsDialog(ViewModel.PageAreaBackground.Color, FileAssociationService.IsRegistered(), ViewModel.PageMode, ViewModel.WheelZoom)
        {
            XamlRoot = Content.XamlRoot,
        };

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary)
        {
            return;
        }

        ViewModel.UpdateBackgroundColor(dialog.SelectedColor);
        ViewModel.SetWheelZoom(dialog.SelectedWheelZoom);

        if (dialog.SelectedMixedPagesMode != ViewModel.PageMode)
        {
            ViewModel.SetPageMode(dialog.SelectedMixedPagesMode);
            if (ViewModel.IsFitWidthActive)
            {
                // "Adatta a larghezza" is a mode the document stays in: it means something else now.
                await FitWidthToViewportAsync();
            }
        }

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

    private async void OnAboutClicked(object sender, RoutedEventArgs e)
    {
        var dialog = new AboutDialog
        {
            XamlRoot = Content.XamlRoot,
        };

        await dialog.ShowAsync();
    }

    private async void OnSaveFilledFormClicked(object sender, RoutedEventArgs e)
        => await SaveAsAsync(ViewModel.FlattenBeforeSave, "compilato");

    /// <summary>The toolbar's "Salva con nome...": the document as it is now - annotations and filled fields kept editable, nothing flattened.</summary>
    private async void OnSaveAsClicked(object sender, RoutedEventArgs e)
        => await SaveAsAsync(flatten: false, "modificato");

    /// <summary>Asks where to save and writes the document there. False if the user cancelled or the save failed.</summary>
    private async Task<bool> SaveAsAsync(bool flatten, string nameSuffix)
    {
        var picker = new FileSavePicker();
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));

        picker.FileTypeChoices.Add("Documento PDF", new List<string> { ".pdf" });
        picker.SuggestedFileName = ViewModel.DocumentTitle is { } title
            ? $"{Path.GetFileNameWithoutExtension(title)}-{nameSuffix}"
            : $"documento-{nameSuffix}";

        var file = await picker.PickSaveFileAsync();
        if (file is null)
        {
            return false;
        }

        var saved = await ViewModel.SaveFilledFormAsync(file.Path, flatten);
        // When flatten is true, SaveFilledFormAsync already sets its own status message
        // (the panel/filler get torn down at the same time) - do not overwrite it here.
        if (saved && !flatten)
        {
            ViewModel.InfoMessage = $"Salvato in {file.Path}";
        }

        return saved;
    }

    // ---- unsaved changes ---------------------------------------------------------------

    /// <summary>
    /// True if it is fine to replace or close the document: it has no unsaved changes, or the
    /// user saved them, or chose to drop them. False if they cancelled (or the save did not happen).
    /// </summary>
    private async Task<bool> ConfirmLeavingDocumentAsync()
    {
        if (!ViewModel.HasUnsavedChanges)
        {
            return true;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "Modifiche non salvate",
            Content = "Il documento ha modifiche (annotazioni o campi compilati) che non sono state salvate. Il file originale non viene mai modificato: per tenerle vanno salvate in un nuovo file.",
            PrimaryButtonText = "Salva con nome...",
            SecondaryButtonText = "Non salvare",
            CloseButtonText = "Annulla",
            DefaultButton = ContentDialogButton.Primary,
        };

        return await dialog.ShowAsync() switch
        {
            ContentDialogResult.Primary => await SaveAsAsync(flatten: false, "modificato"),
            ContentDialogResult.Secondary => true,
            _ => false,
        };
    }

    private async void OnAppWindowClosing(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
    {
        if (_closeConfirmed || !ViewModel.HasUnsavedChanges)
        {
            return;
        }

        // The dialog is asynchronous, so hold the window open now and close it again once the user has decided.
        args.Cancel = true;
        if (await ConfirmLeavingDocumentAsync())
        {
            _closeConfirmed = true;
            Close();
        }
    }

    // ---- notes and stamps --------------------------------------------------------------

    private void OnStampPresetClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: TextStampPreset preset })
        {
            ViewModel.BeginTextStamp(preset);
        }
    }

    private async void OnPickStampImageClicked(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        foreach (var extension in new[] { ".png", ".jpg", ".jpeg", ".bmp", ".gif" })
        {
            picker.FileTypeFilter.Add(extension);
        }

        picker.SuggestedStartLocation = PickerLocationId.PicturesLibrary;
        var file = await picker.PickSingleFileAsync();
        if (file is null)
        {
            return;
        }

        try
        {
            ViewModel.BeginImageStamp(await DecodeStampImageAsync(file));
        }
        catch (Exception ex)
        {
            ViewModel.StatusMessage = $"Impossibile leggere l'immagine: {ex.Message}";
        }
    }

    /// <summary>Decodes an image file to straight-alpha BGRA, scaled down so its longer side is at most 2000 px - a stamp is a few centimeters wide, and a 40-megapixel photo would bloat the file for nothing.</summary>
    private static async Task<ImageStampData> DecodeStampImageAsync(StorageFile file)
    {
        const double maxSide = 2000;
        using var stream = await file.OpenReadAsync();
        var decoder = await BitmapDecoder.CreateAsync(stream);
        var scale = Math.Min(1.0, maxSide / Math.Max(decoder.PixelWidth, decoder.PixelHeight));
        var transform = new BitmapTransform
        {
            ScaledWidth = (uint)Math.Max(1, Math.Round(decoder.PixelWidth * scale)),
            ScaledHeight = (uint)Math.Max(1, Math.Round(decoder.PixelHeight * scale)),
        };

        var pixels = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, transform, ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
        return new ImageStampData(pixels.DetachPixelData(), (int)transform.ScaledWidth, (int)transform.ScaledHeight, file.Name);
    }

    /// <summary>
    /// The undo/redo buttons are collapsed while the Strumenti panel is closed, and one that is shown again keeps looking
    /// enabled even though it is not (the command says it cannot run; the disabled look is just not applied after being
    /// collapsed). Moving the control to the right visual state by hand once it is shown fixes the look; the command binding keeps
    /// IsEnabled right, and the control follows it from there on.
    /// </summary>
    private void RefreshUndoRedoAppearance()
        => DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            foreach (var (button, canExecute) in new[] { ((Control)UndoButton, ViewModel.CanUndo), (RedoButton, ViewModel.CanRedo) })
            {
                Microsoft.UI.Xaml.VisualStateManager.GoToState(button, canExecute ? "Normal" : "Disabled", false);
                // The new look is not painted until something invalidates the control: nudge it.
                button.Opacity = 0.99;
                button.Opacity = 1;
                button.InvalidateArrange();
                button.UpdateLayout();
            }
        });

    private void OnUndoInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (ViewModel.UndoCommand.CanExecute(null))
        {
            args.Handled = true;
            ViewModel.UndoCommand.Execute(null);
        }
    }

    private void OnRedoInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (ViewModel.RedoCommand.CanExecute(null))
        {
            args.Handled = true;
            ViewModel.RedoCommand.Execute(null);
        }
    }

    private void OnEscapeInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (ViewModel.IsPlacingAnnotation)
        {
            ViewModel.CancelAnnotationTool();
            args.Handled = true;
        }
        else if (ViewModel.SelectedAnnotation is not null)
        {
            ViewModel.SelectAnnotation(null);
            args.Handled = true;
        }
    }

    /// <summary>CTRL + wheel over the pages zooms (the direction is an option); without CTRL the wheel scrolls as usual.</summary>
    private async void OnPagesPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (!e.KeyModifiers.HasFlag(Windows.System.VirtualKeyModifiers.Control))
        {
            return;
        }

        e.Handled = true;
        await ViewModel.ZoomByWheelAsync(e.GetCurrentPoint(PagesScrollViewer).Properties.MouseWheelDelta);
    }

    private void OnPagesPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (ViewModel.SelectedAnnotation is null)
        {
            return;
        }

        for (var element = e.OriginalSource as DependencyObject; element is not null; element = VisualTreeHelper.GetParent(element))
        {
            if (element is FrameworkElement { Tag: PageAnnotationViewModel })
            {
                return; // on an annotation (or its handle): that has its own say
            }
        }

        ViewModel.SelectAnnotation(null);
    }
}
