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

    [Fact]
    public void SetComboSelectionAndRender_OnNonComboField_Throws()
    {
        using var document = PdfDocument.CreateEmpty();
        using var filler = new PdfFormFiller(document);
        var fakeTextField = new PdfFormField(0, 0, "fake", PdfFormFieldType.Text, "", IsChecked: false, IsMultiline: false, 0, 0, 0, 0);

        Assert.Throws<NotSupportedException>(() => filler.SetComboSelectionAndRender(fakeTextField, 0, 10, 10));
    }

    [Fact]
    public void SetListSelectionAndRender_OnNonListField_Throws()
    {
        using var document = PdfDocument.CreateEmpty();
        using var filler = new PdfFormFiller(document);
        var fakeTextField = new PdfFormField(0, 0, "fake", PdfFormFieldType.Text, "", IsChecked: false, IsMultiline: false, 0, 0, 0, 0);

        Assert.Throws<NotSupportedException>(() => filler.SetListSelectionAndRender(fakeTextField, 0, 10, 10));
    }

    [Fact]
    public void ClickButtonAndRender_OnNonButtonField_Throws()
    {
        using var document = PdfDocument.CreateEmpty();
        using var filler = new PdfFormFiller(document);
        var fakeTextField = new PdfFormField(0, 0, "fake", PdfFormFieldType.Text, "", IsChecked: false, IsMultiline: false, 0, 0, 0, 0);

        Assert.Throws<NotSupportedException>(() => filler.ClickButtonAndRender(fakeTextField, 10, 10));
    }

    // Fixtures/form-choices.pdf: an editable combo box "City" that already holds "New York", a
    // MULTI-select list box "Language" and a single-select list box "Level". Changing a combo box
    // that already had a value used to be silently ignored (only the first pick into an empty combo
    // box worked), and FORM_SetIndexSelected alone only *adds* to a multi-select list's selection.
    private static string FixturePath => Path.Combine(AppContext.BaseDirectory, "Fixtures", "form-choices.pdf");

    [Fact]
    public void SetComboSelectionAndRender_ChangesAnExistingValue_EveryTime()
    {
        using var document = PdfDocument.OpenFromPath(FixturePath);
        using var filler = new PdfFormFiller(document);
        var city = filler.ListFields().Single(f => f.Name == "City");
        var options = filler.GetOptions(city);
        Assert.Equal("New York", city.Value);

        foreach (var index in new[] { 2, 1, 3, 0 })
        {
            filler.SetComboSelectionAndRender(city, index, 200, 300);

            var current = filler.ListFields().Single(f => f.Name == "City");
            Assert.Equal(options[index], current.Value);
            Assert.Equal(new[] { index }, filler.GetSelectedOptionIndices(current));
        }
    }

    [Theory]
    [InlineData("Language")] // multi-select list box: a new pick must replace, not add to, the selection
    [InlineData("Level")]    // single-select list box
    public void SetListSelectionAndRender_KeepsExactlyTheLastPick(string fieldName)
    {
        using var document = PdfDocument.OpenFromPath(FixturePath);
        using var filler = new PdfFormFiller(document);
        var list = filler.ListFields().Single(f => f.Name == fieldName);

        foreach (var index in new[] { 3, 1, 0, 2 })
        {
            filler.SetListSelectionAndRender(list, index, 200, 300);

            Assert.Equal(new[] { index }, filler.GetSelectedOptionIndices(list));
        }
    }

    [Fact]
    public void SetComboSelectionAndRender_WithIndexOutOfRange_Throws()
    {
        using var document = PdfDocument.OpenFromPath(FixturePath);
        using var filler = new PdfFormFiller(document);
        var city = filler.ListFields().Single(f => f.Name == "City");

        Assert.Throws<ArgumentOutOfRangeException>(() => filler.SetComboSelectionAndRender(city, 99, 200, 300));
        Assert.Throws<ArgumentOutOfRangeException>(() => filler.SetComboSelectionAndRender(city, -1, 200, 300));
    }

    [Fact]
    public void SetComboSelectionAndRender_SurvivesSaveAndReopen()
    {
        var saved = Path.Combine(Path.GetTempPath(), $"pdf26-test-{Guid.NewGuid():N}.pdf");
        try
        {
            using (var document = PdfDocument.OpenFromPath(FixturePath))
            using (var filler = new PdfFormFiller(document))
            {
                var city = filler.ListFields().Single(f => f.Name == "City");
                filler.SetComboSelectionAndRender(city, 2, 200, 300);
                document.SaveAs(saved);
            }

            using var reopened = PdfDocument.OpenFromPath(saved);
            using var reopenedFiller = new PdfFormFiller(reopened);
            Assert.Equal("Berlin", reopenedFiller.ListFields().Single(f => f.Name == "City").Value);
        }
        finally
        {
            File.Delete(saved);
        }
    }
}
