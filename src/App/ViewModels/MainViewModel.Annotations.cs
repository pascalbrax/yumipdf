using System.Runtime.InteropServices.WindowsRuntime;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Media.Imaging;
using PdfEngine;
using PdfEngine.Annotations;
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

        CancelAnnotationTool();

        if (tool is AnnotationTool.TextField or AnnotationTool.CheckBoxField)
        {
            // A form field is not an annotation PDFium can make: it is placed as a placeholder and written at save time.
            PlaceDesignField(pageIndex, xPt, yPt, tool == AnnotationTool.TextField ? PdfFieldKind.Text : PdfFieldKind.CheckBox);
            return;
        }

        var added = await EditPageAsync(
            pageIndex,
            tool == AnnotationTool.Note ? "aggiungere la nota" : "aggiungere il timbro",
            page => tool switch
            {
                AnnotationTool.Note => PdfEditableAnnotations.AddNote(page, xPt, yPt, string.Empty),
                AnnotationTool.TextStamp when preset is not null => PdfEditableAnnotations.AddTextStamp(
                    page, xPt, yPt, preset.Text, preset.Color, addDate ? DateTime.Now.ToString("dd.MM.yyyy") : null),
                AnnotationTool.ImageStamp when image is not null => PdfEditableAnnotations.AddImageStamp(
                    page, xPt, yPt, image.Bgra, image.PixelWidth, image.PixelHeight),
                _ => -1,
            }).ConfigureAwait(true);

        if (added is not { } index || index < 0)
        {
            return;
        }

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
        var done = await EditPageAsync(annotation.Page.Index, "spostare l'annotazione", page =>
        {
            PdfEditableAnnotations.SetRect(page, index, newRect);
            return index;
        }).ConfigureAwait(true);

        if (done is { } kept)
        {
            SelectAnnotation(FindAnnotation(annotation.Page.Index, kept));
        }
    }

    public async Task SetNoteTextAsync(PageAnnotationViewModel annotation, string text)
    {
        if (string.Equals(annotation.Contents, text, StringComparison.Ordinal))
        {
            return;
        }

        var index = annotation.Index;
        var done = await EditPageAsync(annotation.Page.Index, "modificare la nota", page =>
        {
            PdfEditableAnnotations.SetContents(page, index, text);
            return index;
        }).ConfigureAwait(true);

        if (done is { } kept)
        {
            SelectAnnotation(FindAnnotation(annotation.Page.Index, kept));
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
        SelectAnnotation(null);
        await EditPageAsync(annotation.Page.Index, "eliminare l'annotazione", page =>
        {
            PdfEditableAnnotations.Remove(page, index);
            return index;
        }).ConfigureAwait(true);
    }

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
    private Task<int?> EditPageAsync(int pageIndex, string what, Func<PdfPage, int> mutate)
    {
        var previousWork = _renderTask;
        var task = RunPageEditAsync(previousWork, pageIndex, what, mutate);
        _renderTask = task;
        return task;
    }

    private async Task<int?> RunPageEditAsync(Task previousWork, int pageIndex, string what, Func<PdfPage, int> mutate)
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

        IsBusy = true;
        IsProgressIndeterminate = true;
        try
        {
            var (result, annotations, bgra) = await Task.Run(() =>
            {
                int outcome;
                IReadOnlyList<PdfAnnotationInfo> list;
                using (var page = document.GetPage(pageIndex))
                {
                    outcome = mutate(page);
                    list = PdfEditableAnnotations.List(page);
                }

                return (outcome, list, RenderPageBgra(document, formFiller, pageIndex, pixelWidth, pixelHeight));
            }).ConfigureAwait(true);

            var bitmap = new WriteableBitmap(pixelWidth, pixelHeight);
            using (var stream = bitmap.PixelBuffer.AsStream())
            {
                stream.Write(bgra, 0, bgra.Length);
            }

            placeholder.Bitmap = bitmap;
            placeholder.SetAnnotations(annotations);
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
