using iText.Forms;
using iText.Forms.Fields;
using iText.Forms.Fields.Properties;
using iText.Kernel.Colors;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Annot;
using PdfEngine.Annotations;
using ITextField = iText.Forms.Fields.PdfFormField;

namespace PdfEngine.Forms;

public enum PdfFieldKind
{
    Text,
    CheckBox,
}

/// <summary>Everything the designer needs to create a fillable field.</summary>
/// <param name="Name">The field's name - what a reader lists it as, and what a script or an export would use. Unique among the document's fields.</param>
/// <param name="Rect">Where the field sits on its page, in PDF points (origin bottom-left).</param>
/// <param name="Tooltip">Shown by readers when hovering the field; empty for none.</param>
/// <param name="Multiline">Text fields only: the field wraps onto several lines.</param>
/// <param name="FontSize">Text fields only: the type size in points; 0 lets the reader size the text to fit.</param>
/// <param name="CheckedByDefault">Check boxes only: starts ticked.</param>
public sealed record PdfFieldSpec(
    string Name,
    PdfFieldKind Kind,
    PdfRect Rect,
    string Tooltip = "",
    bool Required = false,
    bool ReadOnly = false,
    bool Multiline = false,
    double FontSize = 0,
    bool CheckedByDefault = false);

/// <summary>One edit to a document's form fields; see <see cref="PdfFormDesigner.Apply"/>.</summary>
public abstract record PdfFormChange;

/// <summary>Creates a field on a page (0-based).</summary>
public sealed record AddFieldChange(int PageIndex, PdfFieldSpec Spec) : PdfFormChange;

/// <summary>
/// Changes a field that is already in the document, found by where its widget sits: the page and
/// its position in the page's annotation list - what <see cref="PdfFormField"/> reports. Each
/// property left null is left exactly as it is.
/// </summary>
public sealed record ModifyFieldChange(int PageIndex, int AnnotIndex, string? NewName = null, PdfRect? NewRect = null) : PdfFormChange;

/// <summary>Deletes a field (all of its widgets) found the same way as for <see cref="ModifyFieldChange"/>.</summary>
public sealed record RemoveFieldChange(int PageIndex, int AnnotIndex) : PdfFormChange;

/// <summary>
/// Creates and edits AcroForm fields - something PDFium cannot do (it reads and fills fields, but
/// has no call to make one). The structural work is done by iText, in append mode, on the bytes
/// of a PDF: take them from <see cref="PdfDocument.SaveToBytes"/> so anything changed in memory
/// (annotations, filled values) is included, apply the changes, and open the result again.
/// </summary>
/// <remarks>
/// iText is AGPL-3.0 (see README "Licenze di terze parti"): distributing an app that includes it
/// means offering its sources under the AGPL too, or buying iText's commercial license. PDFsharp,
/// the MIT alternative, was evaluated and cannot do this: every form-field constructor in its
/// public API is <c>internal</c>.
/// </remarks>
public static class PdfFormDesigner
{
    private const float BorderWidth = 1f;

    /// <summary>
    /// Applies <paramref name="changes"/> to the PDF in <paramref name="pdf"/> and returns the new
    /// file. Existing fields are addressed by their position in the original, so removals and
    /// edits are applied first and additions last; addition order is kept.
    /// </summary>
    /// <exception cref="PdfException">The file cannot be read or edited (damaged, or password protected), or a change names a field that is not there.</exception>
    public static byte[] Apply(byte[] pdf, IReadOnlyList<PdfFormChange> changes)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        ArgumentNullException.ThrowIfNull(changes);
        if (changes.Count == 0)
        {
            return pdf;
        }

        try
        {
            using var input = new MemoryStream(pdf);
            using var output = new MemoryStream();
            var writer = new PdfWriter(output);
            writer.SetCloseStream(false); // the bytes are read back from `output` once the document is closed
            using (var reader = new PdfReader(input))
            using (writer)
            using (var document = new iText.Kernel.Pdf.PdfDocument(reader, writer, new StampingProperties().UseAppendMode()))
            {
                var form = PdfAcroForm.GetAcroForm(document, createIfNotExist: true);

                // Resolve every existing field before touching any: removing one shifts the annotation indices of the rest.
                var existing = new List<(PdfFormChange Change, ITextField Field, PdfWidgetAnnotation Widget)>();
                foreach (var change in changes)
                {
                    switch (change)
                    {
                        case ModifyFieldChange m:
                            existing.Add((change, FieldAt(document, m.PageIndex, m.AnnotIndex, out var widgetM), widgetM));
                            break;
                        case RemoveFieldChange r:
                            existing.Add((change, FieldAt(document, r.PageIndex, r.AnnotIndex, out var widgetR), widgetR));
                            break;
                    }
                }

                foreach (var (change, field, widget) in existing)
                {
                    if (change is ModifyFieldChange modify)
                    {
                        if (modify.NewName is { } name && name != field.GetFieldName().ToUnicodeString())
                        {
                            RequireFreeName(form, name);
                            field.SetFieldName(name);
                        }

                        if (modify.NewRect is { } rect)
                        {
                            widget.SetRectangle(new PdfArray(new[] { (float)rect.Left, (float)rect.Bottom, (float)rect.Right, (float)rect.Top }));
                            field.RegenerateField();
                        }
                    }
                }

                foreach (var (change, field, _) in existing)
                {
                    if (change is RemoveFieldChange)
                    {
                        form.RemoveField(field.GetFieldName().ToUnicodeString());
                    }
                }

                foreach (var add in changes.OfType<AddFieldChange>())
                {
                    RequireFreeName(form, add.Spec.Name);
                    form.AddField(CreateField(document, add.PageIndex, add.Spec));
                }
            }

            return output.ToArray();
        }
        catch (PdfException)
        {
            throw;
        }
        catch (Exception ex) when (ex is iText.Kernel.Exceptions.PdfException or iText.Commons.Exceptions.ITextException or InvalidCastException or ArgumentException)
        {
            throw new PdfException(PdfErrorCode.Unknown, $"Impossibile modificare i campi del documento: {ex.Message}");
        }
    }

    /// <summary>
    /// A field name must be a usable one and not already taken: a dot would make a reader read it
    /// as a hierarchy ("a.b" is field b of group a), and two fields with one name are, to a reader,
    /// one field shown twice - typing in either fills both.
    /// </summary>
    private static void RequireFreeName(PdfAcroForm form, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Contains('.'))
        {
            throw new PdfException(PdfErrorCode.Unknown, $"Nome di campo non valido: '{name}' (non può essere vuoto né contenere punti).");
        }

        if (form.GetField(name) is not null)
        {
            throw new PdfException(PdfErrorCode.Unknown, $"Esiste già un campo chiamato '{name}'.");
        }
    }

    private static ITextField FieldAt(iText.Kernel.Pdf.PdfDocument document, int pageIndex, int annotIndex, out PdfWidgetAnnotation widget)
    {
        if (pageIndex < 0 || pageIndex >= document.GetNumberOfPages())
        {
            throw new PdfException(PdfErrorCode.Unknown, $"Pagina {pageIndex} inesistente.");
        }

        var annotations = document.GetPage(pageIndex + 1).GetAnnotations();
        if (annotIndex < 0 || annotIndex >= annotations.Count || annotations[annotIndex] is not PdfWidgetAnnotation found)
        {
            throw new PdfException(PdfErrorCode.Unknown, $"Nessun campo alla posizione {annotIndex} della pagina {pageIndex}.");
        }

        widget = found;
        // A widget is its own field when it carries the field entries itself, else its parent is the field.
        var dictionary = found.GetPdfObject();
        var fieldDictionary = dictionary.ContainsKey(PdfName.FT) || dictionary.ContainsKey(PdfName.T) ? dictionary : dictionary.GetAsDictionary(PdfName.Parent) ?? dictionary;
        return ITextField.MakeFormField(fieldDictionary, document)
            ?? throw new PdfException(PdfErrorCode.Unknown, $"La posizione {annotIndex} della pagina {pageIndex} non è un campo modulo.");
    }

    private static ITextField CreateField(iText.Kernel.Pdf.PdfDocument document, int pageIndex, PdfFieldSpec spec)
    {
        if (pageIndex < 0 || pageIndex >= document.GetNumberOfPages())
        {
            throw new PdfException(PdfErrorCode.Unknown, $"Pagina {pageIndex} inesistente.");
        }

        var rectangle = new iText.Kernel.Geom.Rectangle((float)spec.Rect.Left, (float)spec.Rect.Bottom, (float)spec.Rect.Width, (float)spec.Rect.Height);
        ITextField field;
        switch (spec.Kind)
        {
            case PdfFieldKind.Text:
                var text = new TextFormFieldBuilder(document, spec.Name).SetWidgetRectangle(rectangle).SetPage(pageIndex + 1);
                field = spec.Multiline ? text.CreateMultilineText() : text.CreateText();
                if (spec.FontSize > 0)
                {
                    field.SetFontSize((float)spec.FontSize);
                }
                else
                {
                    field.SetFontSizeAutoScale();
                }

                break;
            case PdfFieldKind.CheckBox:
                field = new CheckBoxFormFieldBuilder(document, spec.Name).SetWidgetRectangle(rectangle).SetPage(pageIndex + 1).CreateCheckBox();
                field.SetCheckType(CheckBoxType.CHECK);
                if (spec.CheckedByDefault)
                {
                    field.SetValue("Yes");
                }

                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(spec));
        }

        var look = field.GetFirstFormAnnotation();
        look.SetBorderColor(ColorConstants.BLACK);
        look.SetBorderWidth(BorderWidth);
        look.SetBackgroundColor(ColorConstants.WHITE);
        if (spec.Tooltip.Length > 0)
        {
            field.SetAlternativeName(spec.Tooltip);
        }

        if (spec.Required)
        {
            field.SetRequired(true);
        }

        if (spec.ReadOnly)
        {
            field.SetReadOnly(true);
        }

        // Without this the widget has no appearance stream of its own: readers that do not draw a field
        // from its properties (most non-PDFium ones) would show nothing at all.
        field.RegenerateField();
        return field;
    }
}
