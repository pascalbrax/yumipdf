using CommunityToolkit.Mvvm.ComponentModel;
using PdfEngine;
using PdfEngine.Editing;

namespace Pdf26.ViewModels;

/// <summary>
/// "Modifica testo" (M3): choosing the tool, reading the lines of text of each page, replacing or
/// deleting one. The PDFium side is <see cref="PdfTextEditing"/> (and its remarks on fonts: a line whose
/// own font lacks a typed glyph is rewritten in a bundled open-source font); the on-page interaction is
/// <c>PdfTextEditLayer</c>.
/// </summary>
public partial class MainViewModel
{
    /// <summary>Whether "Modifica testo" is on: the lines of text on the pages can be selected, edited and deleted. Excludes the placement tools.</summary>
    [ObservableProperty]
    public partial bool IsTextEditTool { get; set; }

    /// <summary>The line the user last clicked, framed on its page; null when none. Identified by page and <see cref="PdfTextLine.ObjectIndex"/>.</summary>
    [ObservableProperty]
    public partial TextLineSelection? SelectedTextLine { get; set; }

    partial void OnIsTextEditToolChanged(bool value)
    {
        SelectedTextLine = null;
        if (value)
        {
            CancelAnnotationTool();
            SelectAnnotation(null);
            foreach (var page in Pages)
            {
                _ = EnsureTextLinesAsync(page);
            }
        }
        else
        {
            foreach (var page in Pages)
            {
                page.SetTextLines(null);
            }
        }

        OnPropertyChanged(nameof(AnnotationHint));
    }

    /// <summary>Reads a page's text lines (queued behind whatever render or edit is running, with no progress bar: it is quick and invisible) unless they are already there.</summary>
    public Task EnsureTextLinesAsync(PdfPageViewModel page)
    {
        if (page.TextLines is not null || _document is not { } document)
        {
            return Task.CompletedTask;
        }

        var previousWork = _renderTask;
        var task = ReadTextLinesAsync(previousWork, document, page);
        _renderTask = task;
        return task;
    }

    private async Task ReadTextLinesAsync(Task previousWork, PdfDocument document, PdfPageViewModel page)
    {
        try
        {
            await previousWork.ConfigureAwait(true);
        }
        catch
        {
            // Already surfaced by whoever ran it.
        }

        if (!IsTextEditTool || page.TextLines is not null || !ReferenceEquals(document, _document))
        {
            return;
        }

        try
        {
            var lines = await Task.Run(() =>
            {
                using var pdfPage = document.GetPage(page.Index);
                return PdfTextEditing.List(pdfPage);
            }).ConfigureAwait(true);

            if (IsTextEditTool && ReferenceEquals(document, _document))
            {
                page.SetTextLines(lines);
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Impossibile leggere il testo della pagina {page.Index + 1}: {ex.Message}";
        }
    }

    public void SelectTextLine(PdfPageViewModel page, PdfTextLine? line)
    {
        var next = line is null ? null : new TextLineSelection(page, line.ObjectIndex);
        if (!Equals(SelectedTextLine, next))
        {
            SelectedTextLine = next;
        }
    }

    /// <summary>Replaces a line's text. An empty text deletes the line. Returns whether anything changed.</summary>
    public async Task<bool> ReplaceTextLineAsync(PdfPageViewModel page, PdfTextLine line, string newText)
    {
        if (string.IsNullOrWhiteSpace(newText))
        {
            return await DeleteTextLineAsync(page, line).ConfigureAwait(true);
        }

        if (string.Equals(newText.Trim(), line.Text, StringComparison.Ordinal))
        {
            return false;
        }

        PdfTextEditResult? outcome = null;
        var done = await EditPageAsync(page.Index, "modificare il testo", pdfPage =>
        {
            outcome = PdfTextEditing.Replace(pdfPage, line.ObjectIndex, newText);
            return line.ObjectIndex;
        }, textChanged: true).ConfigureAwait(true);

        if (done is null || outcome is null)
        {
            return false;
        }

        PushUndo(TextEditEntry(page.Index, $"modifica del testo a pagina {page.Index + 1}", outcome.Undo));
        ReportTextEdit(outcome);
        SelectedTextLine = new TextLineSelection(page, line.ObjectIndex);
        return true;
    }

    /// <summary>Removes a line from the page for good (it can be undone).</summary>
    public async Task<bool> DeleteTextLineAsync(PdfPageViewModel page, PdfTextLine line)
    {
        PdfEditRecord? record = null;
        SelectedTextLine = null;
        var done = await EditPageAsync(page.Index, "eliminare la riga", pdfPage =>
        {
            record = PdfTextEditing.Remove(pdfPage, line.ObjectIndex);
            return line.ObjectIndex;
        }, textChanged: true).ConfigureAwait(true);

        if (done is null || record is null)
        {
            return false;
        }

        PushUndo(TextEditEntry(page.Index, $"eliminazione di una riga a pagina {page.Index + 1}", record));
        return true;
    }

    /// <summary>Tells the user when the line was rewritten in another font, and about characters nothing could draw.</summary>
    private void ReportTextEdit(PdfTextEditResult outcome)
    {
        if (outcome.MissingCharacters.Length > 0)
        {
            StatusMessage = $"Nessun font disponibile ha questi caratteri (appaiono come riquadri vuoti): {outcome.MissingCharacters}";
        }

        if (outcome.UsedFallbackFont)
        {
            InfoMessage = "Riga riscritta con un font incorporato dall'app: il font del documento non ha tutti i caratteri digitati.";
        }
    }

    private UndoEntry TextEditEntry(int pageIndex, string description, PdfEditRecord record)
        => new(
            description,
            undo: () => Succeeded(EditPageAsync(pageIndex, "annullare la modifica", page => { record.Undo(page); return 0; }, textChanged: true)),
            redo: () => Succeeded(EditPageAsync(pageIndex, "ripetere la modifica", page => { record.Redo(page); return 0; }, textChanged: true)),
            resource: record);

    /// <summary>Turns an edit that reports failure by returning null (and saying why in the status bar) into one that throws, so undo/redo know it did not happen.</summary>
    private static async Task Succeeded(Task<int?> edit)
    {
        if (await edit.ConfigureAwait(true) is null)
        {
            throw new InvalidOperationException("la pagina non è stata modificata.");
        }
    }
}

/// <summary>A line of text on a page, picked out for selection: the line's index among the page's objects stays valid across edits that do not add or remove lines (a rewrite keeps it).</summary>
public sealed record TextLineSelection(PdfPageViewModel Page, int ObjectIndex);
