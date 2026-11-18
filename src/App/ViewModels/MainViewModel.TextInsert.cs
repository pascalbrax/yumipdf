using CommunityToolkit.Mvvm.ComponentModel;
using PdfEngine.Annotations;
using PdfEngine.Editing;

namespace Pdf26.ViewModels;

/// <summary>A color the panel offers for a new line of text.</summary>
public sealed record InsertTextColorPreset(string Name, PdfColor Color)
{
    /// <summary>A fresh brush each time: brushes are UI objects and must not be shared between elements.</summary>
    public Microsoft.UI.Xaml.Media.SolidColorBrush Brush => new(Windows.UI.Color.FromArgb(Color.A, Color.R, Color.G, Color.B));
}

/// <summary>
/// "Aggiungi una riga di testo" (M3): a new line of text at a spot the user clicks on a page. Unlike notes and stamps it is
/// <em>page content</em> (<see cref="PdfTextEditing.Insert"/>): once placed it is a line like those the document had, so
/// "Modifica testo" rewrites or deletes it and Ctrl+Z takes it away - it does not move with the mouse. The size and the color
/// are chosen in the tools panel before the click; the typing happens in a small box the page's annotation layer opens at the
/// clicked spot (<c>PdfAnnotationLayer</c>), which calls <see cref="InsertTextAsync"/>.
/// </summary>
public partial class MainViewModel
{
    public static IReadOnlyList<InsertTextColorPreset> InsertTextColors { get; } = new[]
    {
        new InsertTextColorPreset("Nero", new PdfColor(0, 0, 0)),
        new InsertTextColorPreset("Grigio", new PdfColor(100, 100, 100)),
        new InsertTextColorPreset("Rosso", new PdfColor(200, 0, 0)),
        new InsertTextColorPreset("Arancione", new PdfColor(230, 120, 0)),
        new InsertTextColorPreset("Verde", new PdfColor(0, 128, 0)),
        new InsertTextColorPreset("Blu", new PdfColor(0, 90, 200)),
        new InsertTextColorPreset("Viola", new PdfColor(120, 50, 160)),
    };

    /// <summary>Size, in points, of the next line of text. Kept for the session.</summary>
    [ObservableProperty]
    public partial double InsertTextSize { get; set; } = 12;

    /// <summary>Color of the next line of text (one of <see cref="InsertTextColors"/>). Kept for the session.</summary>
    [ObservableProperty]
    public partial InsertTextColorPreset InsertTextColor { get; set; } = InsertTextColors[0];

    /// <summary>The "Aggiungi una riga di testo" toggle: on while the tool waits for the click that says where the line goes.</summary>
    public bool IsInsertTextToolActive
    {
        get => ActiveAnnotationTool == AnnotationTool.TextLine;
        set
        {
            if (value)
            {
                BeginInsertText();
            }
            else if (ActiveAnnotationTool == AnnotationTool.TextLine)
            {
                CancelAnnotationTool();
            }
        }
    }

    public void BeginInsertText()
    {
        if (_document is null)
        {
            return;
        }

        SelectedAnnotation = null;
        ActiveAnnotationTool = AnnotationTool.TextLine;
    }

    /// <summary>The size to use, as a sane number: what is typed in the panel can be empty, zero or huge.</summary>
    private double EffectiveInsertTextSize => double.IsFinite(InsertTextSize) ? Math.Clamp(InsertTextSize, 4, 200) : 12;

    /// <summary>
    /// Adds <paramref name="text"/> to a page with the top-left corner of the letters at (<paramref name="xPt"/>, <paramref name="yPt"/>)
    /// - PDF points on the page as shown, origin bottom-left - in the size and color chosen in the panel. With the alignment grid on,
    /// the corner is moved to the nearest grid point. An empty text adds nothing. Returns whether a line was added.
    /// </summary>
    public async Task<bool> InsertTextAsync(int pageIndex, double xPt, double yPt, string text)
    {
        if (string.IsNullOrWhiteSpace(text) || pageIndex < 0 || pageIndex >= Pages.Count)
        {
            return false;
        }

        var size = EffectiveInsertTextSize;
        var color = InsertTextColor.Color;
        if (IsGridActive)
        {
            xPt = PageGrid.SnapX(xPt);
            yPt = PageGrid.SnapTop(yPt, Pages[pageIndex].PointHeight);
        }

        PdfTextInsertResult? outcome = null;
        var done = await EditPageAsync(pageIndex, "inserire il testo", page =>
        {
            outcome = PdfTextEditing.Insert(page, xPt, yPt, text, size, color);
            return outcome.ObjectIndex;
        }, textChanged: true).ConfigureAwait(true);

        if (done is null || outcome is null)
        {
            return false;
        }

        PushUndo(TextEditEntry(pageIndex, $"inserimento di una riga di testo a pagina {pageIndex + 1}", outcome.Undo));
        if (outcome.MissingCharacters.Length > 0)
        {
            StatusMessage = $"Nessun font disponibile ha questi caratteri (appaiono come riquadri vuoti): {outcome.MissingCharacters}";
        }

        // The new line is selected in "Modifica testo", so it can be dragged to the right place at once, like a stamp.
        IsTextEditTool = true;
        SelectedTextLine = new TextLineSelection(Pages[pageIndex], outcome.ObjectIndex);
        InfoMessage ??= "Riga aggiunta: trascinala per spostarla, doppio clic per modificarla.";
        return true;
    }
}
