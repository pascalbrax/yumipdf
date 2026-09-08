using PdfEngine;
using PdfEngine.Forms;
using Xunit;

namespace PdfEngine.Tests;

public class PdfFormFillerTests
{
    [Fact]
    public void ListFields_OnDocumentWithNoAcroForm_ReturnsEmpty()
    {
        // A document with no AcroForm at all is the one fixture available without bundling a
        // real fillable PDF (see docs/ROADMAP.md M2 - a small bundled sample form would let
        // this file also cover SetFieldValue/SetChecked/Flatten, which today are verified
        // manually against a real municipal form - see docs/ARCHITECTURE.md "AcroForm
        // filling"). This still exercises FPDFDOC_InitFormFillEnvironment end to end with the
        // real (non-null) FPDF_FORMFILLINFO callbacks wired up in PdfFormFiller's constructor.
        using var document = PdfDocument.CreateEmpty();
        using var filler = new PdfFormFiller(document);

        Assert.Empty(filler.ListFields());
    }

    [Fact]
    public void SetFieldValueAndRender_OnNonTextField_Throws()
    {
        using var document = PdfDocument.CreateEmpty();
        using var filler = new PdfFormFiller(document);
        var fakeCheckboxField = new PdfFormField(0, 0, "fake", PdfFormFieldType.CheckBox, "Off", IsChecked: false, IsMultiline: false, 0, 0, 0, 0);

        Assert.Throws<NotSupportedException>(() => filler.SetFieldValueAndRender(fakeCheckboxField, "x", 10, 10));
    }

    [Fact]
    public void SetCheckedAndRender_OnTextField_Throws()
    {
        using var document = PdfDocument.CreateEmpty();
        using var filler = new PdfFormFiller(document);
        var fakeTextField = new PdfFormField(0, 0, "fake", PdfFormFieldType.Text, "", IsChecked: false, IsMultiline: false, 0, 0, 0, 0);

        Assert.Throws<NotSupportedException>(() => filler.SetCheckedAndRender(fakeTextField, true, 10, 10));
    }
}
