using PdfEngine;
using PdfEngine.Annotations;
using PdfEngine.Forms;
using Xunit;

namespace PdfEngine.Tests;

public class PdfFormDesignerTests
{
    private const int W = 595, H = 842;

    private static byte[] FixtureBytes() => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "text-page.pdf"));

    /// <summary>What the app really hands the designer: the document as PDFium writes it.</summary>
    private static byte[] ViaPdfium(byte[] original)
    {
        using var document = PdfDocument.OpenFromBytes(original);
        return document.SaveToBytes();
    }

    /// <summary>A two-page PDF written with iText's full compression: cross-reference stream and object streams, the modern layout.</summary>
    private static byte[] CompressedTwoPagePdf()
    {
        using var output = new MemoryStream();
        var writer = new iText.Kernel.Pdf.PdfWriter(output, new iText.Kernel.Pdf.WriterProperties().SetFullCompressionMode(true));
        writer.SetCloseStream(false);
        using (writer)
        using (var document = new iText.Kernel.Pdf.PdfDocument(writer))
        {
            document.AddNewPage(new iText.Kernel.Geom.PageSize(595, 842));
            document.AddNewPage(new iText.Kernel.Geom.PageSize(595, 842));
        }

        return output.ToArray();
    }

    private static PdfFieldSpec TextSpec(string name = "Cognome", PdfRect? rect = null, bool multiline = false)
        => new(name, PdfFieldKind.Text, rect ?? new PdfRect(100, 600, 300, 624), Multiline: multiline);

    private static IReadOnlyList<PdfFormField> ListFields(byte[] pdf)
    {
        using var document = PdfDocument.OpenFromBytes(pdf);
        using var filler = new PdfFormFiller(document);
        return filler.ListFields();
    }

    [Fact]
    public void AddField_Text_IsReadBackByPdfiumWithItsNameTypeAndPlace()
    {
        var result = PdfFormDesigner.Apply(ViaPdfium(FixtureBytes()), new PdfFormChange[] { new AddFieldChange(0, TextSpec()) });

        var field = Assert.Single(ListFields(result));
        Assert.Equal("Cognome", field.Name);
        Assert.Equal(PdfFormFieldType.Text, field.Type);
        Assert.Equal(0, field.PageIndex);
        Assert.Equal(100, field.RectLeft, 1);
        Assert.Equal(600, field.RectBottom, 1);
        Assert.Equal(300, field.RectRight, 1);
        Assert.Equal(624, field.RectTop, 1);
    }

    [Fact]
    public void AddField_CheckBox_IsACheckBox_AndTicksByDefaultOnRequest()
    {
        var changes = new PdfFormChange[]
        {
            new AddFieldChange(0, new PdfFieldSpec("Accetto", PdfFieldKind.CheckBox, new PdfRect(100, 550, 116, 566))),
            new AddFieldChange(0, new PdfFieldSpec("Newsletter", PdfFieldKind.CheckBox, new PdfRect(100, 500, 116, 516), CheckedByDefault: true)),
        };
        var result = PdfFormDesigner.Apply(ViaPdfium(FixtureBytes()), changes);

        Assert.All(ListFields(result), f => Assert.Equal(PdfFormFieldType.CheckBox, f.Type));

        // The state is in the file...
        using (var reader = new iText.Kernel.Pdf.PdfReader(new MemoryStream(result)))
        using (var document = new iText.Kernel.Pdf.PdfDocument(reader))
        {
            var form = iText.Forms.PdfAcroForm.GetAcroForm(document, false);
            Assert.Equal("Yes", form.GetField("Newsletter").GetValueAsString());
            Assert.Equal("Off", form.GetField("Accetto").GetValueAsString());
        }

        // ...and PDFium draws it: the ticked box has ink inside it that the empty one lacks. (PDFium's own
        // IsChecked answer for these fields was not trusted: it reported false for the ticked one too.)
        using var pdfium = PdfDocument.OpenFromBytes(result);
        using var filler = new PdfFormFiller(pdfium);
        var bgra = filler.RenderPageToBgra(0, W, H);
        Assert.True(Ink(bgra, 100, 500, 116, 516) > Ink(bgra, 100, 550, 116, 566) + 10);
    }

    private static int Ink(byte[] bgra, double left, double bottom, double right, double top)
    {
        var count = 0;
        for (var y = (int)(H - top); y < (int)(H - bottom); y++)
        {
            for (var x = (int)left; x < (int)right; x++)
            {
                if (bgra[(y * W + x) * 4] < 128)
                {
                    count++;
                }
            }
        }

        return count;
    }

    [Fact]
    public void AddField_Multiline_IsReportedAsMultiline()
    {
        var changes = new PdfFormChange[]
        {
            new AddFieldChange(0, TextSpec("Riga")),
            new AddFieldChange(0, TextSpec("Note", new PdfRect(100, 400, 300, 500), multiline: true)),
        };

        var fields = ListFields(PdfFormDesigner.Apply(ViaPdfium(FixtureBytes()), changes));

        Assert.False(fields.Single(f => f.Name == "Riga").IsMultiline);
        Assert.True(fields.Single(f => f.Name == "Note").IsMultiline);
    }

    [Fact]
    public void AddField_ACreatedTextFieldCanBeFilledByPdfium()
    {
        var result = PdfFormDesigner.Apply(ViaPdfium(FixtureBytes()), new PdfFormChange[] { new AddFieldChange(0, TextSpec()) });

        using var document = PdfDocument.OpenFromBytes(result);
        using var filler = new PdfFormFiller(document);
        var field = filler.ListFields().Single();
        filler.SetFieldValueAndRender(field, "Rossi", W, H);

        Assert.Equal("Rossi", filler.ListFields().Single().Value);
    }

    [Fact]
    public void AddField_ACreatedCheckBoxCanBeTickedByPdfium()
    {
        var result = PdfFormDesigner.Apply(
            ViaPdfium(FixtureBytes()),
            new PdfFormChange[] { new AddFieldChange(0, new PdfFieldSpec("Accetto", PdfFieldKind.CheckBox, new PdfRect(100, 550, 120, 570))) });

        using var document = PdfDocument.OpenFromBytes(result);
        using var filler = new PdfFormFiller(document);
        filler.SetCheckedAndRender(filler.ListFields().Single(), true, W, H);

        Assert.True(filler.ListFields().Single().IsChecked);
    }

    [Fact]
    public void Apply_LeavesThePageContentAndAnnotationsAlone()
    {
        byte[] withNote;
        using (var document = PdfDocument.OpenFromBytes(FixtureBytes()))
        {
            using (var page = document.GetPage(0))
            {
                PdfEditableAnnotations.AddNote(page, 300, 300, "resta");
            }

            withNote = document.SaveToBytes();
        }

        byte[] before;
        using (var document = PdfDocument.OpenFromBytes(withNote))
        using (var page = document.GetPage(0))
        {
            before = page.RenderToBgra(W, H, renderFormFields: false);
        }

        var result = PdfFormDesigner.Apply(withNote, new PdfFormChange[] { new AddFieldChange(0, TextSpec()) });

        using var after = PdfDocument.OpenFromBytes(result);
        using var afterPage = after.GetPage(0);
        Assert.Equal(before, afterPage.RenderToBgra(W, H, renderFormFields: false));
        var note = Assert.Single(PdfEditableAnnotations.List(afterPage));
        Assert.Equal("resta", note.Contents);
    }

    [Fact]
    public void Apply_WorksOnADocumentWithACrossReferenceStream()
    {
        var compressed = CompressedTwoPagePdf();
        Assert.Contains("/XRef", System.Text.Encoding.Latin1.GetString(compressed));

        // Straight from the file, and after PDFium rewrote it - the app always goes through the latter.
        foreach (var input in new[] { compressed, ViaPdfium(compressed) })
        {
            var result = PdfFormDesigner.Apply(input, new PdfFormChange[]
            {
                new AddFieldChange(0, TextSpec("Uno")),
                new AddFieldChange(1, TextSpec("Due")),
            });

            var fields = ListFields(result);
            Assert.Equal(new[] { "Uno", "Due" }, fields.OrderBy(f => f.PageIndex).Select(f => f.Name));
            Assert.Equal(new[] { 0, 1 }, fields.OrderBy(f => f.PageIndex).Select(f => f.PageIndex));
        }
    }

    [Fact]
    public void Modify_MovesAndRenamesAnExistingField_WithoutTouchingTheOthers()
    {
        var created = PdfFormDesigner.Apply(ViaPdfium(FixtureBytes()), new PdfFormChange[]
        {
            new AddFieldChange(0, TextSpec("Primo", new PdfRect(100, 700, 300, 724))),
            new AddFieldChange(0, TextSpec("Secondo", new PdfRect(100, 600, 300, 624))),
        });
        var second = ListFields(created).Single(f => f.Name == "Secondo");

        var result = PdfFormDesigner.Apply(ViaPdfium(created), new PdfFormChange[]
        {
            new ModifyFieldChange(second.PageIndex, second.AnnotIndex, NewName: "Cognome", NewRect: new PdfRect(150, 400, 350, 440)),
        });

        var fields = ListFields(result);
        Assert.Equal(2, fields.Count);
        var first = fields.Single(f => f.Name == "Primo");
        Assert.Equal(700, first.RectBottom, 1);
        var moved = fields.Single(f => f.Name == "Cognome");
        Assert.Equal(150, moved.RectLeft, 1);
        Assert.Equal(400, moved.RectBottom, 1);
        Assert.Equal(350, moved.RectRight, 1);
        Assert.Equal(440, moved.RectTop, 1);
    }

    [Fact]
    public void Remove_DeletesTheField_AndOnlyThatOne()
    {
        var created = PdfFormDesigner.Apply(ViaPdfium(FixtureBytes()), new PdfFormChange[]
        {
            new AddFieldChange(0, TextSpec("Tiene", new PdfRect(100, 700, 300, 724))),
            new AddFieldChange(0, TextSpec("Via", new PdfRect(100, 600, 300, 624))),
        });
        var doomed = ListFields(created).Single(f => f.Name == "Via");

        var result = PdfFormDesigner.Apply(ViaPdfium(created), new PdfFormChange[] { new RemoveFieldChange(doomed.PageIndex, doomed.AnnotIndex) });

        Assert.Equal("Tiene", Assert.Single(ListFields(result)).Name);
    }

    [Fact]
    public void Apply_SetsTooltipAndRequiredAndReadOnlyFlags()
    {
        var spec = new PdfFieldSpec("Codice", PdfFieldKind.Text, new PdfRect(100, 600, 300, 624), Tooltip: "Il tuo codice cliente", Required: true, ReadOnly: true, FontSize: 14);

        var result = PdfFormDesigner.Apply(ViaPdfium(FixtureBytes()), new PdfFormChange[] { new AddFieldChange(0, spec) });

        using var reader = new iText.Kernel.Pdf.PdfReader(new MemoryStream(result));
        using var document = new iText.Kernel.Pdf.PdfDocument(reader);
        var field = iText.Forms.PdfAcroForm.GetAcroForm(document, false).GetField("Codice");
        Assert.Equal("Il tuo codice cliente", field.GetAlternativeName().ToUnicodeString());
        Assert.True(field.IsRequired());
        Assert.True(field.IsReadOnly());
        Assert.Equal(14, field.GetFontSize(), 1);
    }

    [Fact]
    public void Apply_WithNoChanges_ReturnsTheSameBytes()
    {
        var input = ViaPdfium(FixtureBytes());

        Assert.Same(input, PdfFormDesigner.Apply(input, Array.Empty<PdfFormChange>()));
    }

    [Fact]
    public void Apply_ToANonexistentField_ThrowsInsteadOfDamagingTheFile()
    {
        var input = ViaPdfium(FixtureBytes());

        Assert.Throws<PdfException>(() => PdfFormDesigner.Apply(input, new PdfFormChange[] { new RemoveFieldChange(0, 5) }));
        Assert.Throws<PdfException>(() => PdfFormDesigner.Apply(input, new PdfFormChange[] { new AddFieldChange(3, TextSpec()) }));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("a.b")]
    public void AddField_WithAnUnusableName_Throws(string name)
    {
        Assert.Throws<PdfException>(() => PdfFormDesigner.Apply(ViaPdfium(FixtureBytes()), new PdfFormChange[] { new AddFieldChange(0, TextSpec(name)) }));
    }

    [Fact]
    public void AddField_WithANameAlreadyTaken_Throws_AndSoDoesARename()
    {
        var created = PdfFormDesigner.Apply(ViaPdfium(FixtureBytes()), new PdfFormChange[]
        {
            new AddFieldChange(0, TextSpec("Uno", new PdfRect(100, 700, 300, 724))),
            new AddFieldChange(0, TextSpec("Due", new PdfRect(100, 600, 300, 624))),
        });
        var due = ListFields(created).Single(f => f.Name == "Due");

        Assert.Throws<PdfException>(() => PdfFormDesigner.Apply(created, new PdfFormChange[] { new AddFieldChange(0, TextSpec("Uno", new PdfRect(100, 500, 300, 524))) }));
        Assert.Throws<PdfException>(() => PdfFormDesigner.Apply(created, new PdfFormChange[] { new ModifyFieldChange(0, due.AnnotIndex, NewName: "Uno") }));
        Assert.Throws<PdfException>(() => PdfFormDesigner.Apply(ViaPdfium(FixtureBytes()), new PdfFormChange[]
        {
            new AddFieldChange(0, TextSpec("Stesso", new PdfRect(100, 700, 300, 724))),
            new AddFieldChange(0, TextSpec("Stesso", new PdfRect(100, 600, 300, 624))),
        }));
    }
}
