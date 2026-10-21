using System.Runtime.InteropServices.WindowsRuntime;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Media.Imaging;
using PdfEngine;
using PdfEngine.Annotations;
using PdfEngine.Editing;
using PdfEngine.Forms;

namespace Pdf26.ViewModels;

/// <summary>Which annotation the next click on a page will place.</summary>
public enum AnnotationTool
{
    None,
    Note,
    TextStamp,
    ImageStamp,
    TextField,
    CheckBoxField,
}

/// <summary>A ready-made text stamp: the wording and the color of its type and frame.</summary>
public sealed record TextStampPreset(string Text, PdfColor Color)
{
    /// <summary>The preset's color as a brush, for the panel button that picks it.</summary>
    public Microsoft.UI.Xaml.Media.SolidColorBrush TextBrush
        => new(Windows.UI.Color.FromArgb(Color.A, Color.R, Color.G, Color.B));
}

/// <summary>An image chosen for stamping, already decoded to straight-alpha BGRA.</summary>
public sealed record ImageStampData(byte[] Bgra, int PixelWidth, int PixelHeight, string Name);

/// <summary>
/// Notes and stamps (M3): choosing a tool, placing, selecting, moving, editing and deleting
/// them, and keeping track of whether the document has changes that are not saved yet. The PDFium
/// side is <see cref="PdfEditableAnnotations"/>; the on-page interaction is <c>PdfAnnotationLayer</c>.
/// </summary>
public partial class MainViewModel
{
    /// <summary>The stamp wordings offered in the toolbar menu.</summary>
    public static IReadOnlyList<TextStampPreset> TextStampPresets { get; } = new[]
    {
        new TextStampPreset("APPROVATO", new PdfColor(0, 128, 0)),
        new TextStampPreset("RIFIUTATO", new PdfColor(200, 0, 0)),
        new TextStampPreset("BOZZA", new PdfColor(0, 90, 200)),
        new TextStampPreset("CONFIDENZIALE", new PdfColor(200, 0, 0)),
        new TextStampPreset("COPIA", new PdfColor(90, 90, 90)),
        new TextStampPreset("URGENTE", new PdfColor(230, 120, 0)),
    };

    /// <summary>How many of <see cref="TextStampPresets"/> the tools panel shows before the user asks for the rest.</summary>
    private const int BasicStampCount = 2;

    /// <summary>The stamps always shown in the tools panel.</summary>
    public static IReadOnlyList<TextStampPreset> BasicTextStampPresets { get; } = TextStampPresets.Take(BasicStampCount).ToList();

    /// <summary>The other stamps, shown only once the user expands the list ("Mostra altri timbri").</summary>
    public static IReadOnlyList<TextStampPreset> MoreTextStampPresets { get; } = TextStampPresets.Skip(BasicStampCount).ToList();

    /// <summary>Whether the tools panel lists every stamp. Remembered between sessions.</summary>
    [ObservableProperty]
    public partial bool AreMoreStampsExpanded { get; set; }

    public string MoreStampsToggleText => AreMoreStampsExpanded ? "Mostra meno timbri" : "Mostra altri timbri";

    /// <summary>Chevron glyph (Segoe Fluent Icons): down to expand, up to collapse.</summary>
    public string MoreStampsToggleGlyph => AreMoreStampsExpanded ? "\uE70E" : "\uE70D";

    partial void OnAreMoreStampsExpandedChanged(bool value)
    {
        OnPropertyChanged(nameof(MoreStampsToggleText));
        OnPropertyChanged(nameof(MoreStampsToggleGlyph));
        if (_restoringSettings)
        {
            return;
        }

        _settings.MoreStampsExpanded = value;
        SaveSettings();
    }

    private TextStampPreset? _pendingTextStamp;
    private ImageStampData? _pendingImageStamp;

    [ObservableProperty]
    public partial AnnotationTool ActiveAnnotationTool { get; set; }

    /// <summary>Whether text stamps get the date under their wording (toolbar menu toggle; this session only).</summary>
    [ObservableProperty]
    public partial bool AddDateToStamps { get; set; }

    /// <summary>The annotation the user last clicked, framed on its page; null when none.</summary>
    [ObservableProperty]
    public partial PageAnnotationViewModel? SelectedAnnotation { get; set; }

    /// <summary>Whether the open document has changes (annotations, filled fields) that no "Salva con nome" has written out yet.</summary>
    [ObservableProperty]
    public partial bool HasUnsavedChanges { get; set; }

    public bool IsPlacingAnnotation => ActiveAnnotationTool != AnnotationTool.None;

    /// <summary>The user's choice for the tools panel (toolbar "Strumenti" button): notes and stamps to place on the page. Not remembered between sessions - it only means something with a document open.</summary>
    [ObservableProperty]
    public partial bool IsToolsPanelOpen { get; set; }

    /// <summary>What the sidebar actually shows: the user's choice, but never without a document.</summary>
    public bool IsToolsPaneOpen => IsToolsPanelOpen && _document is not null;

    partial void OnIsToolsPanelOpenChanged(bool value)
    {
        if (value)
        {
            CloseOtherPanels(SidebarPanel.Tools);
        }

        NotifyToolsPane();
    }

    private void NotifyToolsPane()
    {
        OnPropertyChanged(nameof(IsToolsPaneOpen));
        OnPropertyChanged(nameof(IsLeftPaneOpen));
        OnPropertyChanged(nameof(LeftPaneWidth));
    }

    /// <summary>The annotation and save buttons only make sense with a document open.</summary>
    public Microsoft.UI.Xaml.Visibility DocumentToolsVisibility => _document is null
        ? Microsoft.UI.Xaml.Visibility.Collapsed
        : Microsoft.UI.Xaml.Visibility.Visible;

    /// <summary>The toolbar's "Nota" toggle: on while the note tool is the active one.</summary>
    public bool IsNoteToolActive
    {
        get => ActiveAnnotationTool == AnnotationTool.Note;
        set
        {
            if (value)
            {
                BeginNote();
            }
            else if (ActiveAnnotationTool == AnnotationTool.Note)
            {
                CancelAnnotationTool();
            }
        }
    }

    /// <summary>What to tell the user while a tool waits for a click on the page.</summary>
    public string? AnnotationHint => ActiveAnnotationTool switch
    {
        AnnotationTool.Note => "Fai clic sulla pagina per inserire la nota · Esc per annullare",
        AnnotationTool.TextStamp => $"Fai clic sulla pagina per inserire il timbro \"{_pendingTextStamp?.Text}\" · Esc per annullare",
        AnnotationTool.ImageStamp => $"Fai clic sulla pagina per inserire l'immagine \"{_pendingImageStamp?.Name}\" · Esc per annullare",
        AnnotationTool.TextField => "Fai clic sulla pagina per inserire il campo di testo · Esc per annullare",
        AnnotationTool.CheckBoxField => "Fai clic sulla pagina per inserire la casella di controllo · Esc per annullare",
        _ => null,
    };

    /// <summary>Raised after a note was added: the page's layer opens its text editor on it.</summary>
    public event Action<PageAnnotationViewModel>? NoteEditRequested;

    /// <summary>Raised when the user asks to edit a note's text (double click, Enter, menu).</summary>
    public void RequestNoteEdit(PageAnnotationViewModel annotation) => NoteEditRequested?.Invoke(annotation);

    partial void OnActiveAnnotationToolChanged(AnnotationTool value)
    {
        if (value != AnnotationTool.None)
        {
            IsTextEditTool = false;
        }

        OnPropertyChanged(nameof(IsPlacingAnnotation));
        OnPropertyChanged(nameof(IsNoteToolActive));
        OnPropertyChanged(nameof(AnnotationHint));
        NotifyFieldTools();
    }

    public void BeginNote()
    {
        if (_document is null)
        {
            return;
        }

        SelectedAnnotation = null;
        ActiveAnnotationTool = AnnotationTool.Note;
    }

    public void BeginTextStamp(TextStampPreset preset)
    {
        if (_document is null)
        {
            return;
        }

        SelectedAnnotation = null;
        _pendingTextStamp = preset;
        ActiveAnnotationTool = AnnotationTool.TextStamp;
        OnPropertyChanged(nameof(AnnotationHint));
    }

    public void BeginImageStamp(ImageStampData image)
    {
        if (_document is null)
        {
            return;
        }

        SelectedAnnotation = null;
        _pendingImageStamp = image;
        ActiveAnnotationTool = AnnotationTool.ImageStamp;
        OnPropertyChanged(nameof(AnnotationHint));
    }

    public void CancelAnnotationTool()
    {
        _pendingTextStamp = null;
        _pendingImageStamp = null;
        ActiveAnnotationTool = AnnotationTool.None;
    }

    public void SelectAnnotation(PageAnnotationViewModel? annotation)
    {
        if (!ReferenceEquals(SelectedAnnotation, annotation))
        {
            SelectedAnnotation = annotation;
        }
    }

    /// <summary>Forgets tool, selection and unsaved-changes state - a different document is replacing the one that had them.</summary>
    private void ResetAnnotationState()
    {
        OnPropertyChanged(nameof(DocumentToolsVisibility));
        NotifyToolsPane();
        CancelAnnotationTool();
        SelectedAnnotation = null;
        _annotationRecipes.Clear();
        IsTextEditTool = false;
        SelectedTextLine = null;
        ClearUndoHistory();
        ResetFieldDesign();
        HasUnsavedChanges = false;
    }

    /// <summary>Places the active tool's annotation with its center at (<paramref name="xPt"/>, <paramref name="yPt"/>) on a page. A tool is single-shot: it ends with the click.</summary>
    public async Task PlaceAnnotationAsync(int pageIndex, double xPt, double yPt)
    {
        var tool = ActiveAnnotationTool;
        var preset = _pendingTextStamp;
        var image = _pendingImageStamp;
        var addDate = AddDateToStamps;
        if (tool == AnnotationTool.None)
        {
            return;
        }

        // What it takes to make the annotation again (redo, or undoing its deletion).
        AnnotationRecipe? recipe = tool switch
        {
            AnnotationTool.Note => new NoteRecipe(string.Empty),
            AnnotationTool.TextStamp when preset is not null => new TextStampRecipe(preset.Text, preset.Color, addDate ? DateTime.Now.ToString("dd.MM.yyyy") : null),
            AnnotationTool.ImageStamp when image is not null => new ImageStampRecipe(image.Bgra, image.PixelWidth, image.PixelHeight),
            _ => null,
        };

        CancelAnnotationTool();

        if (tool is AnnotationTool.TextField or AnnotationTool.CheckBoxField)
        {
            // A form field is not an annotation PDFium can make: it is placed as a placeholder and written at save time.
            PlaceDesignField(pageIndex, xPt, yPt, tool == AnnotationTool.TextField ? PdfFieldKind.Text : PdfFieldKind.CheckBox);
            return;
        }

        if (recipe is null)
        {
            return;
        }

        string? newId = null;
        var added = await EditPageAsync(
            pageIndex,
            tool == AnnotationTool.Note ? "aggiungere la nota" : "aggiungere il timbro",
            page =>
            {
                var created = AddFromRecipe(page, recipe, xPt, yPt);
                newId = PdfEditableAnnotations.EnsureId(page, created);
                return created;
            }).ConfigureAwait(true);

        if (added is not { } index || index < 0 || newId is null)
        {
            return;
        }

        _annotationRecipes[newId] = recipe;
        PushUndo(AnnotationAddEntry(pageIndex, newId, recipe, xPt, yPt));

        var created = FindAnnotation(pageIndex, index);
        SelectAnnotation(created);
        if (created is { IsNote: true })
        {
            // A note is only worth having with something in it: open its text box right away.
            RequestNoteEdit(created);
        }
    }

    /// <summary>Moves/resizes an annotation to <paramref name="newRect"/> (PDF points); the page may clamp it inside its edges.</summary>
    public async Task MoveAnnotationAsync(PageAnnotationViewModel annotation, PdfRect newRect)
    {
        if (annotation.IsField)
        {
            MoveDesignField(annotation, newRect);
            return;
        }

        var index = annotation.Index;
        var pageIndex = annotation.Page.Index;
        var oldRect = annotation.Rect;
        string? id = null;
        var done = await EditPageAsync(pageIndex, "spostare l'annotazione", page =>
        {
            id = PdfEditableAnnotations.EnsureId(page, index);
            PdfEditableAnnotations.SetRect(page, index, newRect);
            return index;
        }).ConfigureAwait(true);

        if (done is { } kept)
        {
            var moved = FindAnnotation(pageIndex, kept);
            SelectAnnotation(moved);
            var finalRect = moved?.Rect ?? newRect; // the page may have clamped the rectangle
            if (id is not null && finalRect != oldRect)
            {
                PushUndo(AnnotationRectEntry(pageIndex, id, oldRect, finalRect, annotation.IsNote ? "spostamento della nota" : "spostamento del timbro"));
            }
        }
    }

    public async Task SetNoteTextAsync(PageAnnotationViewModel annotation, string text)
    {
        if (string.Equals(annotation.Contents, text, StringComparison.Ordinal))
        {
            return;
        }

        var index = annotation.Index;
        var pageIndex = annotation.Page.Index;
        var oldText = annotation.Contents;
        string? id = null;
        var done = await EditPageAsync(pageIndex, "modificare la nota", page =>
        {
            id = PdfEditableAnnotations.EnsureId(page, index);
            PdfEditableAnnotations.SetContents(page, index, text);
            return index;
        }).ConfigureAwait(true);

        if (done is { } kept)
        {
            SelectAnnotation(FindAnnotation(pageIndex, kept));
            if (id is not null)
            {
                PushUndo(NoteTextEntry(pageIndex, id, oldText, text));
            }
        }
    }

    public async Task DeleteAnnotationAsync(PageAnnotationViewModel annotation)
    {
        if (annotation.IsField)
        {
            DeleteDesignField(annotation);
            return;
        }

        var index = annotation.Index;
        var pageIndex = annotation.Page.Index;
        var rect = annotation.Rect;
        string? id = null;
        SelectAnnotation(null);
        var done = await EditPageAsync(pageIndex, "eliminare l'annotazione", page =>
        {
            id = PdfEditableAnnotations.EnsureId(page, index);
            PdfEditableAnnotations.Remove(page, index);
            return index;
        }).ConfigureAwait(true);

        if (done is null || id is null)
        {
            return;
        }

        // Deleting an empty note right after creating it (its text box was closed without writing anything) cancels
        // that creation out, instead of leaving two entries to step through. Only for an empty note: anything with
        // content, a stamp included, must stay recoverable.
        if (annotation.IsNote && annotation.Contents.Length == 0
            && _undoStack.Count > 0 && _undoStack[^1].Tag is AnnotationAddTag created && created.Id == id)
        {
            _undoStack[^1].Dispose();
            _undoStack.RemoveAt(_undoStack.Count - 1);
            NotifyUndoChanged();
            return;
        }

        // A note is rebuilt from its text; a stamp from the recipe it was made with - which exists only for stamps made in this session.
        AnnotationRecipe? recipe = annotation.IsNote
            ? new NoteRecipe(annotation.Contents)
            : _annotationRecipes.GetValueOrDefault(id);
        if (recipe is null)
        {
            // Nothing to rebuild it from (a stamp that was already in the file): undo cannot bring it back, and the older
            // entries address annotations by position, so they go too rather than risk hitting the wrong thing.
            ClearUndoHistory();
            InfoMessage = "Timbro eliminato: non l'ha creato questa sessione, quindi l'eliminazione non si può annullare (la cronologia di annulla/ripeti è stata azzerata).";
            return;
        }

        PushUndo(AnnotationDeleteEntry(pageIndex, id, recipe, rect, annotation.IsNote ? "eliminazione della nota" : "eliminazione del timbro"));
    }

    // ---- undo support for notes and stamps ------------------------------------------------------

    /// <summary>What it takes to make a note or stamp again.</summary>
    private abstract record AnnotationRecipe;

    private sealed record NoteRecipe(string Contents) : AnnotationRecipe;

    private sealed record TextStampRecipe(string Text, PdfColor Color, string? Subtitle) : AnnotationRecipe;

    private sealed record ImageStampRecipe(byte[] Bgra, int PixelWidth, int PixelHeight) : AnnotationRecipe;

    /// <summary>Marks the undo entry that created an annotation, by its identity.</summary>
    private sealed record AnnotationAddTag(string Id);

    /// <summary>The recipes of the annotations made in this session, by identity.</summary>
    private readonly Dictionary<string, AnnotationRecipe> _annotationRecipes = new();

    private static int AddFromRecipe(PdfPage page, AnnotationRecipe recipe, double centerX, double centerY) => recipe switch
    {
        NoteRecipe note => PdfEditableAnnotations.AddNote(page, centerX, centerY, note.Contents),
        TextStampRecipe stamp => PdfEditableAnnotations.AddTextStamp(page, centerX, centerY, stamp.Text, stamp.Color, stamp.Subtitle),
        ImageStampRecipe image => PdfEditableAnnotations.AddImageStamp(page, centerX, centerY, image.Bgra, image.PixelWidth, image.PixelHeight),
        _ => throw new InvalidOperationException("Annotazione sconosciuta."),
    };

    private static int RequireIndex(PdfPage page, string id)
    {
        var index = PdfEditableAnnotations.IndexOfId(page, id);
        return index >= 0 ? index : throw new InvalidOperationException("l'annotazione non c'è più.");
    }

    private UndoEntry AnnotationAddEntry(int pageIndex, string id, AnnotationRecipe recipe, double x, double y)
        => new(
            recipe is NoteRecipe ? "inserimento di una nota" : "inserimento di un timbro",
            undo: () => Succeeded(EditPageAsync(pageIndex, "annullare l'inserimento", page => { PdfEditableAnnotations.Remove(page, RequireIndex(page, id)); return 0; })),
            redo: () => Succeeded(EditPageAsync(pageIndex, "ripetere l'inserimento", page =>
            {
                var added = AddFromRecipe(page, recipe, x, y);
                PdfEditableAnnotations.SetId(page, added, id);
                return added;
            })),
            tag: new AnnotationAddTag(id));

    private UndoEntry AnnotationRectEntry(int pageIndex, string id, PdfRect before, PdfRect after, string description)
        => new(
            description,
            undo: () => Succeeded(EditPageAsync(pageIndex, "annullare lo spostamento", page => { var i = RequireIndex(page, id); PdfEditableAnnotations.SetRect(page, i, before); return i; })),
            redo: () => Succeeded(EditPageAsync(pageIndex, "ripetere lo spostamento", page => { var i = RequireIndex(page, id); PdfEditableAnnotations.SetRect(page, i, after); return i; })));

    private UndoEntry NoteTextEntry(int pageIndex, string id, string before, string after)
        => new(
            "modifica del testo di una nota",
            undo: () => Succeeded(EditPageAsync(pageIndex, "annullare la modifica", page => { var i = RequireIndex(page, id); PdfEditableAnnotations.SetContents(page, i, before); return i; })),
            redo: () => Succeeded(EditPageAsync(pageIndex, "ripetere la modifica", page => { var i = RequireIndex(page, id); PdfEditableAnnotations.SetContents(page, i, after); return i; })));

    private UndoEntry AnnotationDeleteEntry(int pageIndex, string id, AnnotationRecipe recipe, PdfRect rect, string description)
        => new(
            description,
            undo: () => Succeeded(EditPageAsync(pageIndex, "ripristinare l'annotazione", page =>
            {
                // Rebuilt around the old rectangle's center, then given back its exact size and place and its identity.
                var added = AddFromRecipe(page, recipe, (rect.Left + rect.Right) / 2, (rect.Bottom + rect.Top) / 2);
                PdfEditableAnnotations.SetId(page, added, id);
                PdfEditableAnnotations.SetRect(page, added, rect);
                return added;
            })),
            redo: () => Succeeded(EditPageAsync(pageIndex, "ripetere l'eliminazione", page => { PdfEditableAnnotations.Remove(page, RequireIndex(page, id)); return 0; })));

    private void ClearAllPageAnnotations()
    {
        SelectedAnnotation = null;
        foreach (var page in Pages)
        {
            page.SetAnnotations(Array.Empty<PdfAnnotationInfo>());
        }
    }

    private PageAnnotationViewModel? FindAnnotation(int pageIndex, int annotationIndex)
        => pageIndex >= 0 && pageIndex < Pages.Count
            ? Pages[pageIndex].Annotations.FirstOrDefault(a => a.Index == annotationIndex)
            : null;

    /// <summary>
    /// Runs <paramref name="mutate"/> on a page, then re-reads that page's annotations and
    /// re-renders it - queued behind whatever render or edit is in flight, like a form-field
    /// edit (see <see cref="EnqueueFieldEditAsync"/>), because PDFium is touched by one piece of
    /// work at a time. Returns what <paramref name="mutate"/> returned, or null if it failed (the
    /// status bar says what could not be done).
    /// </summary>
    private Task<int?> EditPageAsync(int pageIndex, string what, Func<PdfPage, int> mutate, bool textChanged = false)
    {
        var previousWork = _renderTask;
        var task = RunPageEditAsync(previousWork, pageIndex, what, mutate, textChanged);
        _renderTask = task;
        return task;
    }

    /// <param name="textChanged">The edit changed the page's text: the lines "Modifica testo" shows and the search index are brought up to date too.</param>
    private async Task<int?> RunPageEditAsync(Task previousWork, int pageIndex, string what, Func<PdfPage, int> mutate, bool textChanged)
    {
        try
        {
            await previousWork.ConfigureAwait(true);
        }
        catch
        {
            // Already surfaced by whoever ran it; this only waits for the document to be free.
        }

        var document = _document;
        var formFiller = _formFiller;
        if (document is null || pageIndex < 0 || pageIndex >= Pages.Count)
        {
            return null;
        }

        var placeholder = Pages[pageIndex];
        var pixelWidth = (int)Math.Round(placeholder.PixelWidth);
        var pixelHeight = (int)Math.Round(placeholder.PixelHeight);
        var relistText = IsTextEditTool;

        IsBusy = true;
        IsProgressIndeterminate = true;
        try
        {
            var (result, annotations, bgra, textLines, pageText) = await Task.Run(() =>
            {
                int outcome;
                IReadOnlyList<PdfAnnotationInfo> list;
                IReadOnlyList<PdfTextLine>? lines = null;
                string? text = null;
                using (var page = document.GetPage(pageIndex))
                {
                    outcome = mutate(page);
                    list = PdfEditableAnnotations.List(page);
                    if (relistText)
                    {
                        lines = PdfTextEditing.List(page);
                    }

                    if (textChanged)
                    {
                        text = page.HasExtractableText() ? page.ExtractText() : string.Empty;
                    }
                }

                return (outcome, list, RenderPageBgra(document, formFiller, pageIndex, pixelWidth, pixelHeight), lines, text);
            }).ConfigureAwait(true);

            var bitmap = new WriteableBitmap(pixelWidth, pixelHeight);
            using (var stream = bitmap.PixelBuffer.AsStream())
            {
                stream.Write(bgra, 0, bgra.Length);
            }

            placeholder.Bitmap = bitmap;
            placeholder.SetAnnotations(annotations);
            if (relistText || textChanged)
            {
                placeholder.SetTextLines(textLines);
            }

            if (pageText is not null)
            {
                // The search index follows the edit (a page with native text is indexed by its own extracted text).
                if (pageText.Length > 0)
                {
                    _pageText[pageIndex] = pageText;
                }
                else
                {
                    _pageText.Remove(pageIndex);
                }

                RefreshSearchMatches(resetPosition: false);
            }

            MarkThumbnailDirty(pageIndex);
            HasUnsavedChanges = true;
            return result;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Impossibile {what}: {ex.Message}";
            return null;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
