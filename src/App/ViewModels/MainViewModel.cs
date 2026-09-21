using System.Collections.ObjectModel;
using System.Runtime.InteropServices.WindowsRuntime;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.UI;
using Pdf26.Services;
using PdfEngine;
using PdfEngine.Forms;

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
    private readonly AppSettings _settings;
    private bool _restoringSettings;
    private PdfDocument? _document;
    private PdfFormFiller? _formFiller;
    private PdfSource? _lastResolvedSource;
    private string? _lastResolvedLocalPath;
    private CancellationTokenSource? _renderCts;
    private Task _renderTask = Task.CompletedTask;
    private double _maxZoomForDocument = MaxZoom;

    public ObservableCollection<PdfPageViewModel> Pages { get; } = new();

    public ObservableCollection<FormFieldEntryViewModel> FormFields { get; } = new();

    [ObservableProperty]
    public partial bool IsFormPanelOpen { get; set; }

    /// <summary>
    /// The on-page overlay is only useful once the user has opened "Compila modulo" - showing
    /// every field's frame over the page by default was visually noisy for documents nobody
    /// intends to fill in. Pushes the new visibility to every field rather than each one
    /// reading <see cref="IsFormPanelOpen"/> itself - see <see cref="FormFieldEntryViewModel.OverlayVisibility"/>.
    /// </summary>
    partial void OnIsFormPanelOpenChanged(bool value)
    {
        var visibility = value ? Visibility.Visible : Visibility.Collapsed;
        foreach (var field in FormFields)
        {
            field.OverlayVisibility = visibility;
        }
    }

    /// <summary>Same reasoning as <see cref="ProgressVisibility"/> - computed here rather than a converter bound from x:Bind.</summary>
    public Visibility FormButtonVisibility => FormFields.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    [ObservableProperty]
    public partial int CurrentPageIndex { get; set; }

    [ObservableProperty]
    public partial int PageCount { get; set; }

    [ObservableProperty]
    public partial string? DocumentTitle { get; set; }

    /// <summary>Shown in the status bar in an error color - reserve for actual failures, not confirmations (see <see cref="InfoMessage"/>).</summary>
    [ObservableProperty]
    public partial string? StatusMessage { get; set; }

    /// <summary>Neutral-colored status bar text for non-error confirmations (e.g. a completed save) - kept separate from <see cref="StatusMessage"/> so a success does not visually read as an error.</summary>
    [ObservableProperty]
    public partial string? InfoMessage { get; set; }

    /// <summary>Whether the status bar's progress indicator should be visible at all (resolving/opening a source, or rendering pages).</summary>
    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    /// <summary>Spinning ("we don't know how long this takes yet" - resolving/opening) vs a determinate percentage (rendering, page count known).</summary>
    [ObservableProperty]
    public partial bool IsProgressIndeterminate { get; set; } = true;

    /// <summary>0-100. Only meaningful while <see cref="IsProgressIndeterminate"/> is <c>false</c>.</summary>
    [ObservableProperty]
    public partial double ProgressPercent { get; set; }

    /// <summary>
    /// Exposed instead of a plain bool + XAML converter: x:Bind's codegen for a
    /// StaticResource-based converter fails when the page root is a <c>Window</c> (not a
    /// <c>FrameworkElement</c>, which is what WinUI3's Window is) - verified while building
    /// this feature (CS1503 "cannot convert MainWindow to FrameworkElement" in the generated
    /// MainWindow.g.cs). Computing Visibility here sidesteps it entirely.
    /// </summary>
    public Visibility ProgressVisibility => IsBusy ? Visibility.Visible : Visibility.Collapsed;

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(ProgressVisibility));

    /// <summary>Whether "Salva con nome..." flattens the form first - see <see cref="SaveFilledFormAsync"/>.</summary>
    [ObservableProperty]
    public partial bool FlattenBeforeSave { get; set; } = true;

    [ObservableProperty]
    public partial SolidColorBrush PageAreaBackground { get; set; } = new(Colors.Gray);

    [ObservableProperty]
    public partial double Zoom { get; set; } = BaseZoom;

    public int ZoomPercent => (int)Math.Round(Zoom / BaseZoom * 100);

    public string ZoomPercentText => $"{ZoomPercent}%";

    public MainViewModel()
    {
        _settings = _settingsService.Load();
        PageAreaBackground = new SolidColorBrush(ColorHex.Parse(_settings.PageAreaBackgroundHex));

        _restoringSettings = true;
        IsThumbnailPanelOpen = _settings.ThumbnailPanelOpen;
        _restoringSettings = false;
    }

    public void UpdateBackgroundColor(Color color)
    {
        PageAreaBackground = new SolidColorBrush(color);
        _settings.PageAreaBackgroundHex = ColorHex.ToHex(color);
        SaveSettings();
    }

    /// <summary>
    /// Always saves the one shared <see cref="_settings"/> object, never a fresh
    /// <c>AppSettings</c> holding just the value that changed: that would silently reset every
    /// other preference to its default. A settings file that cannot be written must not break
    /// whatever the user just did, so IO errors are swallowed.
    /// </summary>
    private void SaveSettings()
    {
        try
        {
            _settingsService.Save(_settings);
        }
        catch (Exception)
        {
        }
    }

    public async Task<OpenOutcome> OpenAsync(PdfSource source, string? password = null, CancellationToken cancellationToken = default)
    {
        StatusMessage = $"Apertura di {source.Value}...";
        InfoMessage = null;
        // Indeterminate while resolving/reading the source: its duration is unknown (a UNC
        // share or an HTTP download can take anywhere from instant to a while). RunRenderAsync
        // switches this to a determinate per-page percentage once the page count is known.
        IsBusy = true;
        IsProgressIndeterminate = true;
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
                // Deliberately no ConfigureAwait(false): OpenAsync keeps touching UI-thread-
                // affine state (ObservableProperty setters that drive x:Bind, and the render
                // call below whose own ConfigureAwait(true) calls only work if there is still
                // a UI SynchronizationContext to return to). Losing the UI thread here was a
                // real bug - the status bar's "Apertura di ..." text stopped reliably clearing
                // once the document had actually finished loading, because everything after
                // this await (including the final `StatusMessage = null`) ran off-thread.
                localPath = await _sourceResolver.ResolveToLocalPathAsync(source, cancellationToken);
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

            await StopThumbnailsAsync().ConfigureAwait(true);
            Thumbnails = null;

            _formFiller?.Dispose();
            _formFiller = null;
            FormFields.Clear();
            IsFormPanelOpen = false;

            _document?.Dispose();
            _document = document;

            DocumentTitle = Path.GetFileName(localPath);
            PageCount = _document.PageCount;
            CurrentPageIndex = 0;
            _maxZoomForDocument = ComputeMaxZoomForDocument(_document);
            Zoom = Math.Min(Zoom, _maxZoomForDocument);

            LoadFormFields(_document);

            await RenderAllPagesAsync(cancellationToken, isNewDocument: true);

            // Thumbnails only start once the main view is completely populated - see BeginThumbnails.
            BeginThumbnails(document);
            StatusMessage = null;
            return OpenOutcome.Success;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Errore nell'apertura del documento: {ex.Message}";
            return OpenOutcome.Failed;
        }
        finally
        {
            IsBusy = false;
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

    // ---- Form filling (M2) --------------------------------------------------------------

    /// <summary>
    /// Builds a <see cref="PdfFormFiller"/> for the newly-opened <paramref name="document"/>
    /// and populates <see cref="FormFields"/> - a no-op (empty list, no filler kept around)
    /// for a document with no AcroForm fields, which is the common case and not an error.
    /// </summary>
    private void LoadFormFields(PdfDocument document)
    {
        try
        {
            var filler = new PdfFormFiller(document);
            var fields = filler.ListFields();
            if (fields.Count == 0)
            {
                filler.Dispose();
                return;
            }

            _formFiller = filler;
            foreach (var field in fields)
            {
                if (field.Type is PdfFormFieldType.ComboBox or PdfFormFieldType.ListBox)
                {
                    var options = filler.GetOptions(field);
                    var selected = filler.GetSelectedOptionIndices(field);
                    FormFields.Add(new FormFieldEntryViewModel(field, this, options, selected));
                }
                else
                {
                    FormFields.Add(new FormFieldEntryViewModel(field, this));
                }
            }
        }
        catch (PdfException)
        {
            // Failing to set up the form-fill environment should not prevent viewing the
            // document itself - it just means no "Compila modulo" panel for this one.
        }
        finally
        {
            OnPropertyChanged(nameof(FormButtonVisibility));
        }
    }

    /// <summary>Commits a text field's edited value and re-renders its page so the change is visible immediately.</summary>
    public Task ApplyFieldTextAsync(FormFieldEntryViewModel entry)
        => EnqueueFieldEditAsync(entry.Field.PageIndex, entry.Field.Name,
            (formFiller, w, h) => formFiller.SetFieldValueAndRender(entry.Field, entry.Value, w, h));

    /// <summary>Commits a checkbox/radio button's new state and re-renders its page.</summary>
    public Task ApplyFieldCheckedAsync(FormFieldEntryViewModel entry)
        => EnqueueFieldEditAsync(entry.Field.PageIndex, entry.Field.Name,
            (formFiller, w, h) => formFiller.SetCheckedAndRender(entry.Field, entry.IsChecked, w, h));

    /// <summary>Commits a combo box's newly picked option and re-renders its page.</summary>
    public Task ApplyComboSelectionAsync(FormFieldEntryViewModel entry)
    {
        // Captured now, not read when the queued edit runs: a quick second pick would otherwise be written twice.
        var optionIndex = entry.SelectedOptionIndex;
        return EnqueueFieldEditAsync(entry.Field.PageIndex, entry.Field.Name,
            (formFiller, w, h) => formFiller.SetComboSelectionAndRender(entry.Field, optionIndex, w, h));
    }

    /// <summary>Commits a list box's newly picked option and re-renders its page.</summary>
    public Task ApplyListSelectionAsync(FormFieldEntryViewModel entry)
    {
        var optionIndex = entry.SelectedOptionIndex;
        return EnqueueFieldEditAsync(entry.Field.PageIndex, entry.Field.Name,
            (formFiller, w, h) => formFiller.SetListSelectionAndRender(entry.Field, optionIndex, w, h));
    }

    /// <summary>
    /// Clicks a push button and re-renders its page - then, since the button's action (a
    /// "Reset Form" action, most commonly) can change any field's value, re-reads every field
    /// from the document and syncs each <see cref="FormFieldEntryViewModel"/> to match, rather
    /// than assuming only this one field could have changed.
    /// </summary>
    public async Task ApplyButtonClickAsync(FormFieldEntryViewModel entry)
    {
        await EnqueueFieldEditAsync(entry.Field.PageIndex, entry.Field.Name,
            (formFiller, w, h) => formFiller.ClickButtonAndRender(entry.Field, w, h)).ConfigureAwait(true);

        var formFiller = _formFiller;
        if (formFiller is null)
        {
            return;
        }

        var freshFields = await Task.Run(() => formFiller.ListFields()).ConfigureAwait(true);
        foreach (var field in FormFields)
        {
            foreach (var fresh in freshFields)
            {
                if (fresh.PageIndex == field.Field.PageIndex && fresh.AnnotIndex == field.Field.AnnotIndex)
                {
                    field.SyncFromField(fresh);
                    break;
                }
            }
        }

        // A reset can change fields on any page, not just this button's own.
        MarkAllThumbnailsDirty();
    }

    /// <summary>
    /// Queues a field edit (+ the page re-render it requires) strictly behind whatever is
    /// currently chained on <see cref="_renderTask"/>, and runs the edit itself on a
    /// background thread rather than synchronously on the caller's (UI) thread.
    /// </summary>
    /// <remarks>
    /// Both used to happen the other way around: <c>PdfFormFiller.SetFieldValue</c>/<c>SetChecked</c>
    /// ran synchronously, directly on the UI thread, before ever awaiting anything. Filling in
    /// a field started a background render that holds <c>PdfiumRuntime.Gate</c> for its
    /// duration; if the *next* field was committed (tabbing
    /// or clicking to a new field) while that render was still in flight, the synchronous
    /// <c>SetFieldValue</c> call for it blocked the UI thread waiting on the very same lock -
    /// freezing the app, and in one observed case bringing the whole process down. Routing the
    /// edit itself through this same background-and-queued path removes the synchronous
    /// UI-thread wait entirely: a fast second edit now just waits its turn in the queue
    /// (asynchronously - the UI stays responsive) instead of blocking the thread that queue
    /// depends on to ever get unblocked.
    /// </remarks>
    private Task EnqueueFieldEditAsync(int pageIndex, string fieldName, Func<PdfFormFiller, int, int, byte[]> editAndRender)
    {
        var previousWork = _renderTask;
        var task = RunFieldEditAsync(previousWork, pageIndex, fieldName, editAndRender);
        _renderTask = task;
        return task;
    }

    private async Task RunFieldEditAsync(Task previousWork, int pageIndex, string fieldName, Func<PdfFormFiller, int, int, byte[]> editAndRender)
    {
        try
        {
            await previousWork.ConfigureAwait(true);
        }
        catch
        {
            // Previous work already surfaced its own errors/cancellation; this await only
            // exists to know when it is safe to touch the document again.
        }

        var formFiller = _formFiller;
        if (formFiller is null || pageIndex < 0 || pageIndex >= Pages.Count)
        {
            return;
        }

        var placeholder = Pages[pageIndex];
        var pixelWidth = (int)Math.Round(placeholder.PixelWidth);
        var pixelHeight = (int)Math.Round(placeholder.PixelHeight);

        IsBusy = true;
        IsProgressIndeterminate = false;
        ProgressPercent = 0;
        try
        {
            var bgra = await Task.Run(() => editAndRender(formFiller, pixelWidth, pixelHeight)).ConfigureAwait(true);

            var bitmap = new WriteableBitmap(pixelWidth, pixelHeight);
            using (var stream = bitmap.PixelBuffer.AsStream())
            {
                stream.Write(bgra, 0, bgra.Length);
            }

            placeholder.Bitmap = bitmap;
            MarkThumbnailDirty(pageIndex);
            ProgressPercent = 100;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Impossibile aggiornare il campo '{fieldName}': {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Saves the document with its current field values. When <paramref name="flatten"/> is
    /// set, every page is flattened first - permanently baking the field values into the page
    /// content and dropping the interactive widgets. That is irreversible for the rest of
    /// this session (the in-memory document really did lose its form), so the form panel and
    /// filler are torn down *before* flattening, not after - flattening a page while PDFium
    /// still has an active interactive form-fill environment/page-view referencing it crashed
    /// the process outright (an unrecoverable native crash, not a catchable .NET exception)
    /// when this was tested live against a real form. The original file on disk is untouched
    /// either way since this always writes to <paramref name="destinationPath"/>, never in place.
    /// </summary>
    public async Task<bool> SaveFilledFormAsync(string destinationPath, bool flatten)
    {
        var document = _document;
        if (document is null)
        {
            return false;
        }

        if (flatten)
        {
            await StopThumbnailsAsync().ConfigureAwait(true);
            _formFiller?.Dispose();
            _formFiller = null;
            FormFields.Clear();
            IsFormPanelOpen = false;
            OnPropertyChanged(nameof(FormButtonVisibility));
        }

        try
        {
            await Task.Run(() =>
            {
                if (flatten)
                {
                    for (var index = 0; index < document.PageCount; index++)
                    {
                        using var page = document.GetPage(index);
                        page.Flatten();
                    }
                }

                document.SaveAs(destinationPath);
            });

            if (flatten)
            {
                InfoMessage = "Documento unito e salvato: i valori compilati sono ora parte della pagina e i campi non sono più modificabili in questa sessione.";
            }

            return true;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Impossibile salvare il documento: {ex.Message}";
            return false;
        }
        finally
        {
            // Flattening bakes the field values into the pages: they look the same, but they are
            // no longer drawn through the (now gone) form filler, so refresh every thumbnail.
            if (flatten)
            {
                RestartThumbnails();
            }
        }
    }

    /// <summary>
    /// Renders one page, routing through <see cref="PdfFormFiller.RenderPageToBgra"/> whenever
    /// a form filler is open on the document - so an edited-but-not-yet-flattened field value
    /// actually shows up (plain <c>PdfPage.RenderToBgra</c> only paints each widget's
    /// last-generated appearance stream, not live edits - see its remarks). Used by both the
    /// whole-document render loop and the single-page refresh after a field edit, so a value
    /// someone just typed does not appear to "revert" the next time the whole document
    /// re-renders (e.g. on a zoom change).
    /// </summary>
    /// <summary>
    /// The renderer printing must use: identical to what the screen shows, so the user's
    /// unflattened form entries end up on paper too (see <see cref="RenderPageBgra"/>).
    /// </summary>
    public byte[] RenderPageForPrint(int pageIndex, int pixelWidth, int pixelHeight)
    {
        var document = _document ?? throw new InvalidOperationException("Nessun documento aperto.");
        return RenderPageBgra(document, _formFiller, pageIndex, pixelWidth, pixelHeight);
    }

    private static byte[] RenderPageBgra(PdfDocument document, PdfFormFiller? formFiller, int pageIndex, int pixelWidth, int pixelHeight)
    {
        if (formFiller is not null)
        {
            return formFiller.RenderPageToBgra(pageIndex, pixelWidth, pixelHeight);
        }

        using var page = document.GetPage(pageIndex);
        return page.RenderToBgra(pixelWidth, pixelHeight);
    }


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
        var formFiller = _formFiller;
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

                // Editable/checkable fields on this page get an in-place overlay drawn on top
                // of the page bitmap (see PdfPageViewModel.Fields) - read-only field types
                // (combo/list/button/signature) are left to the side panel only, since there
                // is nothing to edit in place for them yet.
                foreach (var field in FormFields)
                {
                    if (field.PageIndex != index || !(field.IsTextField || field.IsCheckableField))
                    {
                        continue;
                    }

                    field.UpdateOverlay(zoom, heightPt);
                    placeholder.Fields.Add(field);
                }

                placeholders.Add(placeholder);
                Pages.Add(placeholder);
            }
        }

        // The page count is always known by this point (whether just counted above or reused
        // from the resize branch), so rendering itself is always a determinate progress:
        // this both covers the initial "opening" render (OpenAsync starts out indeterminate
        // above) and the zoom-triggered "recalculating" re-render (ApplyZoomAsync never sets
        // IsBusy itself - this is the only progress indicator it gets).
        IsBusy = true;
        IsProgressIndeterminate = false;
        ProgressPercent = 0;

        try
        {
            var completed = 0;
            foreach (var placeholder in placeholders)
            {
                cts.Token.ThrowIfCancellationRequested();

                var pageIndex = placeholder.Index;
                var pixelWidth = (int)Math.Round(placeholder.PixelWidth);
                var pixelHeight = (int)Math.Round(placeholder.PixelHeight);

                var bgra = await Task.Run(() => RenderPageBgra(document, formFiller, pageIndex, pixelWidth, pixelHeight), cts.Token)
                    .ConfigureAwait(true);

                var bitmap = new WriteableBitmap(pixelWidth, pixelHeight);
                using (var stream = bitmap.PixelBuffer.AsStream())
                {
                    stream.Write(bgra, 0, bgra.Length);
                }

                placeholder.Bitmap = bitmap;

                completed++;
                ProgressPercent = placeholders.Count == 0 ? 100 : 100.0 * completed / placeholders.Count;
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
        finally
        {
            IsBusy = false;
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

    // ---- Thumbnails ---------------------------------------------------------------------

    /// <summary>Below this many pages a thumbnail strip adds nothing over just scrolling the document.</summary>
    private const int MinPagesForThumbnails = 3;

    /// <summary>Pixels of a thumbnail's longer side: sharp enough for a high-DPI display at the strip's size, small enough that a whole document's worth stays cheap.</summary>
    private const int ThumbnailLongSidePixels = 360;

    private CancellationTokenSource? _thumbnailCts;
    private Task _thumbnailLoop = Task.CompletedTask;
    private bool _thumbnailLoopActive;
    private readonly HashSet<int> _thumbnailPending = new();
    private int _thumbnailFocusIndex;

    /// <summary>
    /// The current document's thumbnails; <c>null</c> until the main view has finished loading
    /// it, and for documents with fewer than <see cref="MinPagesForThumbnails"/> pages - which is
    /// also what keeps the whole strip (and its toolbar button) out of sight for those.
    /// </summary>
    [ObservableProperty]
    public partial PdfThumbnailSet? Thumbnails { get; set; }

    /// <summary>The user's choice for the strip (toolbar button/handle); remembered between sessions.</summary>
    [ObservableProperty]
    public partial bool IsThumbnailPanelOpen { get; set; }

    public Visibility ThumbnailButtonVisibility => Thumbnails is not null ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>What the strip's <c>SplitView</c> actually shows: the user's choice, but never for a document without thumbnails.</summary>
    public bool IsThumbnailPaneOpen => IsThumbnailPanelOpen && Thumbnails is not null;

    partial void OnThumbnailsChanged(PdfThumbnailSet? value)
    {
        OnPropertyChanged(nameof(ThumbnailButtonVisibility));
        OnPropertyChanged(nameof(IsThumbnailPaneOpen));
    }

    partial void OnIsThumbnailPanelOpenChanged(bool value)
    {
        OnPropertyChanged(nameof(IsThumbnailPaneOpen));
        if (_restoringSettings)
        {
            return;
        }

        _settings.ThumbnailPanelOpen = value;
        SaveSettings();
    }

    /// <summary>Tells the generator which page the strip is centered on, so the pages the user is about to look at are rendered first.</summary>
    public void SetThumbnailFocus(int pageIndex) => _thumbnailFocusIndex = pageIndex;

    /// <summary>
    /// Starts thumbnail generation - deliberately only once the main view is completely
    /// populated (every page has its bitmap), so thumbnails never compete with the first full
    /// render of the document. A render that was cancelled or failed part-way leaves pages
    /// without a bitmap, which is how this notices and stays out.
    /// </summary>
    private void BeginThumbnails(PdfDocument document)
    {
        if (!ReferenceEquals(document, _document)
            || document.PageCount < MinPagesForThumbnails
            || Pages.Count != document.PageCount
            || Pages.Any(page => page.Bitmap is null))
        {
            return;
        }

        Thumbnails = new PdfThumbnailSet(Pages.Select(page => (page.PointWidth, page.PointHeight)));
        _thumbnailFocusIndex = CurrentPageIndex;
        RestartThumbnails();
    }

    /// <summary>Discards any generation in progress and queues every page again.</summary>
    private void RestartThumbnails()
    {
        if (Thumbnails is null)
        {
            return;
        }

        _thumbnailCts?.Cancel();
        _thumbnailCts = new CancellationTokenSource();

        _thumbnailPending.Clear();
        MarkAllThumbnailsDirty();
    }

    /// <summary>A page's content changed (a field was edited): render its thumbnail again.</summary>
    private void MarkThumbnailDirty(int pageIndex)
    {
        if (Thumbnails is null || pageIndex < 0 || pageIndex >= Thumbnails.Count)
        {
            return;
        }

        _thumbnailPending.Add(pageIndex);
        EnsureThumbnailLoop();
    }

    private void MarkAllThumbnailsDirty()
    {
        if (Thumbnails is null)
        {
            return;
        }

        for (var index = 0; index < Thumbnails.Count; index++)
        {
            _thumbnailPending.Add(index);
        }

        EnsureThumbnailLoop();
    }

    /// <summary>Cancels generation and waits for the in-flight page (at most one) to finish, so the document/filler can then be safely replaced or disposed.</summary>
    private async Task StopThumbnailsAsync()
    {
        _thumbnailCts?.Cancel();
        try
        {
            await _thumbnailLoop.ConfigureAwait(true);
        }
        catch
        {
            // The loop swallows its own errors; nothing to report from a cancelled one.
        }

        _thumbnailPending.Clear();
    }

    private void EnsureThumbnailLoop()
    {
        if (_thumbnailLoopActive
            || _thumbnailPending.Count == 0
            || Thumbnails is not { } set
            || _document is not { } document
            || _thumbnailCts is not { IsCancellationRequested: false } cts)
        {
            return;
        }

        _thumbnailLoopActive = true;
        _thumbnailLoop = RunThumbnailLoopAsync(document, set, cts.Token);
    }

    /// <remarks>
    /// "Separate thread" here means off the UI thread: each page is rendered by a thread-pool
    /// task, one at a time, so the window stays responsive however many pages there are.
    /// It cannot mean a second PDFium thread - the native library is not thread-safe in this
    /// build, so every call still funnels through <c>PdfiumRuntime</c>'s single worker. One
    /// page per call keeps each of those calls short: a zoom change or a field edit only ever
    /// waits behind a single thumbnail. Pages are taken nearest-first around
    /// <see cref="_thumbnailFocusIndex"/>, so the ones about to be shown come first.
    /// </remarks>
    private async Task RunThumbnailLoopAsync(PdfDocument document, PdfThumbnailSet set, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested && TakeNextPendingThumbnail() is { } index)
            {
                try
                {
                    var formFiller = _formFiller;
                    var (width, height) = ThumbnailPixelSize(set[index].AspectRatio);

                    var bgra = await Task.Run(() => RenderPageBgra(document, formFiller, index, width, height), token)
                        .ConfigureAwait(true);
                    if (token.IsCancellationRequested)
                    {
                        break;
                    }

                    var bitmap = new WriteableBitmap(width, height);
                    using (var stream = bitmap.PixelBuffer.AsStream())
                    {
                        stream.Write(bgra, 0, bgra.Length);
                    }

                    set.SetBitmap(index, bitmap);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception)
                {
                    // A thumbnail is decoration: a page that cannot be rendered keeps its
                    // numbered placeholder, and the rest carry on.
                }
            }
        }
        finally
        {
            _thumbnailLoopActive = false;

            // A restart requested while this loop was still winding down found the loop
            // "active" and could not start its own - take over for it.
            if (token.IsCancellationRequested && _thumbnailCts is { IsCancellationRequested: false })
            {
                EnsureThumbnailLoop();
            }
        }
    }

    private int? TakeNextPendingThumbnail()
    {
        if (_thumbnailPending.Count == 0)
        {
            return null;
        }

        var best = -1;
        var bestDistance = int.MaxValue;
        foreach (var index in _thumbnailPending)
        {
            var distance = Math.Abs(index - _thumbnailFocusIndex);
            if (distance < bestDistance || (distance == bestDistance && index > best))
            {
                best = index;
                bestDistance = distance;
            }
        }

        _thumbnailPending.Remove(best);
        return best;
    }

    private static (int Width, int Height) ThumbnailPixelSize(double aspectRatio)
    {
        return aspectRatio >= 1
            ? (Math.Max(1, (int)Math.Round(ThumbnailLongSidePixels / aspectRatio)), ThumbnailLongSidePixels)
            : (ThumbnailLongSidePixels, Math.Max(1, (int)Math.Round(ThumbnailLongSidePixels * aspectRatio)));
    }

    partial void OnCurrentPageIndexChanged(int value)
    {
        PreviousPageCommand.NotifyCanExecuteChanged();
        NextPageCommand.NotifyCanExecuteChanged();
    }

    public void Dispose()
    {
        _renderCts?.Cancel();
        _thumbnailCts?.Cancel();
        _formFiller?.Dispose();
        _formFiller = null;
        _document?.Dispose();
        _document = null;
    }
}
