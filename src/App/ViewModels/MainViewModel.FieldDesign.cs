using PdfEngine;
using PdfEngine.Annotations;
using PdfEngine.Forms;
using Pdf26.Services;

namespace Pdf26.ViewModels;

/// <summary>What the field properties editor hands back.</summary>
public sealed record DesignFieldProperties(
    string Name, string Tooltip, bool Required, bool ReadOnly, bool Multiline, double FontSize, bool CheckedByDefault);

/// <summary>
/// Adding fillable form fields (text fields and check boxes) to the open document. PDFium cannot
/// create fields, so a placed field lives here as a <see cref="DesignField"/> - drawn as a
/// placeholder by the page layer, movable, resizable, editable, deletable - and is written into
/// the file by <see cref="PdfFormDesigner"/> (iText) when the document is saved; the saved file is
/// then opened, so the new fields can be filled in like any other form's.
/// </summary>
/// <remarks>
/// Only fields created in the current session can be edited this way. The fields a document
/// already had are untouched (they stay what the "Compila modulo" panel fills in): showing an
/// existing field moved would need its old drawing hidden from the page until the save, which
/// PDFium has no clean way to do - recorded in docs/ROADMAP.md.
/// </remarks>
public partial class MainViewModel
{
    private const double DefaultTextFieldWidth = 180;
    private const double DefaultTextFieldHeight = 22;
    private const double DefaultCheckBoxSize = 14;
    private const double MinFieldSize = 8;

    private readonly List<DesignField> _designFields = new();

    /// <summary>Whether fields were placed and are waiting for the next save to be written.</summary>
    public bool HasPendingFields => _designFields.Count > 0;

    /// <summary>Raised when the user asks to edit a field's properties (after placing one, double click, Enter, menu).</summary>
    public event Action<PageAnnotationViewModel>? FieldEditRequested;

    public void RequestFieldEdit(PageAnnotationViewModel field) => FieldEditRequested?.Invoke(field);

    /// <summary>The toolbar-panel toggle for the text field tool.</summary>
    public bool IsTextFieldToolActive
    {
        get => ActiveAnnotationTool == AnnotationTool.TextField;
        set => SetFieldTool(AnnotationTool.TextField, value);
    }

    public bool IsCheckBoxFieldToolActive
    {
        get => ActiveAnnotationTool == AnnotationTool.CheckBoxField;
        set => SetFieldTool(AnnotationTool.CheckBoxField, value);
    }

    private void SetFieldTool(AnnotationTool tool, bool on)
    {
        if (on)
        {
            if (_document is not null)
            {
                SelectedAnnotation = null;
                ActiveAnnotationTool = tool;
            }
        }
        else if (ActiveAnnotationTool == tool)
        {
            CancelAnnotationTool();
        }
    }

    private void NotifyFieldTools()
    {
        OnPropertyChanged(nameof(IsTextFieldToolActive));
        OnPropertyChanged(nameof(IsCheckBoxFieldToolActive));
    }

    private void ResetFieldDesign()
    {
        _designFields.Clear();
        OnPropertyChanged(nameof(HasPendingFields));
    }

    // ---- placing and editing ---------------------------------------------------------------

    private void PlaceDesignField(int pageIndex, double xPt, double yPt, PdfFieldKind kind)
    {
        if (pageIndex < 0 || pageIndex >= Pages.Count)
        {
            return;
        }

        var page = Pages[pageIndex];
        var (width, height) = kind == PdfFieldKind.Text
            ? (DefaultTextFieldWidth, DefaultTextFieldHeight)
            : (DefaultCheckBoxSize, DefaultCheckBoxSize);
        var field = new DesignField(pageIndex, kind, NextFieldName(kind), ClampToPage(page, new PdfRect(xPt - width / 2, yPt - height / 2, xPt + width / 2, yPt + height / 2)));
        _designFields.Add(field);
        HasUnsavedChanges = true;
        OnPropertyChanged(nameof(HasPendingFields));

        RefreshDesignItems(pageIndex);
        var item = FindDesignItem(field);
        SelectAnnotation(item);
        if (item is not null)
        {
            // A field is only useful once it has a sensible name: open its properties right away, like a new note's text.
            RequestFieldEdit(item);
        }
    }

    public void MoveDesignField(PageAnnotationViewModel item, PdfRect newRect)
    {
        if (item.Field is not { } field)
        {
            return;
        }

        field.Rect = ClampToPage(item.Page, newRect);
        HasUnsavedChanges = true;
        RefreshDesignItems(field.PageIndex);
        SelectAnnotation(FindDesignItem(field));
    }

    /// <summary>Applies the editor's result; returns the reason it was refused (a bad or taken name), or null when applied.</summary>
    public string? UpdateDesignField(PageAnnotationViewModel item, DesignFieldProperties properties)
    {
        if (item.Field is not { } field)
        {
            return null;
        }

        var error = ValidateFieldName(field, properties.Name);
        if (error is not null)
        {
            return error;
        }

        field.Name = properties.Name.Trim();
        field.Tooltip = properties.Tooltip.Trim();
        field.Required = properties.Required;
        field.ReadOnly = properties.ReadOnly;
        field.Multiline = field.Kind == PdfFieldKind.Text && properties.Multiline;
        field.FontSize = field.Kind == PdfFieldKind.Text ? Math.Max(0, properties.FontSize) : 0;
        field.CheckedByDefault = field.Kind == PdfFieldKind.CheckBox && properties.CheckedByDefault;
        HasUnsavedChanges = true;
        RefreshDesignItems(field.PageIndex);
        SelectAnnotation(FindDesignItem(field));
        return null;
    }

    public void DeleteDesignField(PageAnnotationViewModel item)
    {
        if (item.Field is not { } field)
        {
            return;
        }

        SelectAnnotation(null);
        _designFields.Remove(field);
        OnPropertyChanged(nameof(HasPendingFields));
        RefreshDesignItems(field.PageIndex);
    }

    /// <summary>Why <paramref name="name"/> cannot be this field's name, or null if it can. Names must be unique across every field in the document, the ones it already had included.</summary>
    public string? ValidateFieldName(DesignField field, string name)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0)
        {
            return "Il nome non può essere vuoto.";
        }

        if (trimmed.Contains('.'))
        {
            return "Il nome non può contenere punti.";
        }

        var taken = _designFields.Any(other => !ReferenceEquals(other, field) && other.Name == trimmed)
            || FormFields.Any(existing => existing.Name == trimmed);
        return taken ? "Esiste già un campo con questo nome." : null;
    }

    private string NextFieldName(PdfFieldKind kind)
    {
        var stem = kind == PdfFieldKind.Text ? "Testo" : "Casella";
        for (var number = 1; ; number++)
        {
            var candidate = stem + number;
            if (!_designFields.Any(f => f.Name == candidate) && !FormFields.Any(f => f.Name == candidate))
            {
                return candidate;
            }
        }
    }

    private static PdfRect ClampToPage(PdfPageViewModel page, PdfRect rect)
    {
        var width = Math.Clamp(Math.Max(rect.Width, MinFieldSize), MinFieldSize, page.PointWidth);
        var height = Math.Clamp(Math.Max(rect.Height, MinFieldSize), MinFieldSize, page.PointHeight);
        var left = Math.Clamp(rect.Left, 0, page.PointWidth - width);
        var bottom = Math.Clamp(rect.Bottom, 0, page.PointHeight - height);
        return new PdfRect(left, bottom, left + width, bottom + height);
    }

    /// <summary>Rebuilds a page's placeholder items from the design list, so the page layer redraws.</summary>
    private void RefreshDesignItems(int pageIndex)
    {
        if (pageIndex >= 0 && pageIndex < Pages.Count)
        {
            var page = Pages[pageIndex];
            page.SetDesignFields(_designFields.Where(f => f.PageIndex == pageIndex).Select(f => new PageAnnotationViewModel(page, f)).ToList());
        }
    }

    private PageAnnotationViewModel? FindDesignItem(DesignField field)
        => field.PageIndex < Pages.Count ? Pages[field.PageIndex].DesignFields.FirstOrDefault(i => ReferenceEquals(i.Field, field)) : null;

    // ---- saving ----------------------------------------------------------------------------

    /// <summary>
    /// Saves the document with the placed fields written in, then opens the saved file so they can
    /// be filled in right away. "Unisci" cannot be combined with this: flattening would bake the
    /// fields into the page the moment they were made.
    /// </summary>
    private async Task<bool> SaveWithPendingFieldsAsync(string destinationPath)
    {
        var document = _document;
        if (document is null)
        {
            return false;
        }

        var changes = _designFields.Select(f => (PdfFormChange)new AddFieldChange(f.PageIndex, f.ToSpec())).ToList();
        try
        {
            await Task.Run(() => File.WriteAllBytes(destinationPath, PdfFormDesigner.Apply(document.SaveToBytes(), changes))).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Impossibile salvare il documento con i campi: {ex.Message}";
            return false;
        }

        var outcome = await OpenAsync(new PdfSource(PdfSourceKind.LocalPath, destinationPath)).ConfigureAwait(true);
        InfoMessage = outcome == OpenOutcome.Success
            ? $"Salvato in {destinationPath}: i campi creati sono ora compilabili (aperto il file salvato)."
            : $"Salvato in {destinationPath}, ma non è stato possibile riaprirlo.";
        return true;
    }
}
