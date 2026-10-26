using PdfEngine.Annotations;
using PdfEngine.Forms;
using Printing;
using Xunit;

namespace PdfEngine.Tests;

public class PrintingTests
{
    [Theory]
    [InlineData(595, 842)]
    [InlineData(842, 595)]
    [InlineData(2880, 1728)]
    public void Fit_RespectsAsymmetricPrinterMarginsAndPageProportions(double width, double height)
    {
        var layout = PrintPlacement.Fit(width, height, 794, 1123, 12, 18, 752, 1060);
        Assert.Equal(width / height, layout.Width / layout.Height, 10);
        Assert.True(layout.X >= 12 && layout.Y >= 18);
        Assert.True(layout.X + layout.Width <= 764.000001);
        Assert.True(layout.Y + layout.Height <= 1078.000001);
        Assert.Equal(12 + 752 / 2.0, layout.X + layout.Width / 2, 8);
        Assert.Equal(18 + 1060 / 2.0, layout.Y + layout.Height / 2, 8);
    }

    [Fact]
    public void Fit_ClipsBorderlessAreaToPaper()
    {
        var layout = PrintPlacement.Fit(100, 100, 800, 1000, -10, -10, 830, 1030);
        Assert.Equal(new PrintPlacement(0, 100, 800, 800), layout);
    }

    [Fact]
    public void Fit_RejectsAnEmptyPrintableArea()
        => Assert.Throws<InvalidOperationException>(() => PrintPlacement.Fit(595, 842, 794, 1123, 800, 0, 10, 10));

    [Fact]
    public void RasterSize_BoundsMemoryForLargePaper()
    {
        var layout = PrintPlacement.Fit(2880, 1728, 12000, 9000, 0, 0, 12000, 9000);
        var pixels = layout.RasterSize(600);
        Assert.InRange(pixels.Width, 1, 8192);
        Assert.InRange(pixels.Height, 1, 8192);
        Assert.True((long)pixels.Width * pixels.Height <= 20_000_000);
        Assert.InRange((double)pixels.Width / pixels.Height, 2880.0 / 1728 - 0.001, 2880.0 / 1728 + 0.001);
    }

    [Fact]
    public void Ranges_DoNotPrintOverlappingPagesTwice()
        => Assert.Equal(new[] { 2, 3, 4, 5, 6, 10 }, PrintPageSelection.Resolve(10, new[] { (2, 4), (3, 6), (10, 30) }));

    [Fact]
    public void Ranges_EmptyMeansEntireDocument()
        => Assert.Equal(new[] { 1, 2, 3 }, PrintPageSelection.Resolve(3, Array.Empty<(int, int)>()));

    [Fact]
    public void Ranges_OutOfDocumentIsAnError()
        => Assert.Throws<InvalidOperationException>(() => PrintPageSelection.Resolve(3, new[] { (5, 7) }));

    private static byte[] PageBytes => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "text-page.pdf"));

    [Fact]
    public void Snapshot_RejectsAnEmptyDocument()
    {
        using var empty = PdfDocument.CreateEmpty();
        Assert.Throws<InvalidOperationException>(() => PdfPrintSnapshot.Capture(empty));
    }

    [Fact]
    public void Snapshot_SurvivesClosingOriginalDocument()
    {
        PdfPrintSnapshot snapshot;
        using (var original = PdfDocument.OpenFromBytes(PageBytes))
            snapshot = PdfPrintSnapshot.Capture(original);
        using (snapshot)
        {
            Assert.Equal(1, snapshot.PageCount);
            Assert.Equal(240 * 320 * 4, snapshot.Render(0, 240, 320).Length);
        }
    }

    [Fact]
    public void Snapshot_PreservesFilledValuesAndCheckboxWithoutFlatteningOriginal()
    {
        var fields = new PdfFormChange[]
        {
            new AddFieldChange(0, new PdfFieldSpec("Nome", PdfFieldKind.Text, new PdfRect(50, 500, 350, 540), FontSize: 18)),
            new AddFieldChange(0, new PdfFieldSpec("Conferma", PdfFieldKind.CheckBox, new PdfRect(50, 450, 70, 470))),
        };
        using var original = PdfDocument.OpenFromBytes(PdfFormDesigner.Apply(PageBytes, fields));
        using var filler = new PdfFormFiller(original);
        var existing = filler.ListFields();
        filler.SetCheckedAndRender(existing.Single(f => f.Name == "Conferma"), true, 595, 842);
        // Commit the text last: its edit kills PDFium's focus, removing the checkbox's
        // transient focus rectangle. That UI adornment must not appear on a printout.
        var expected = filler.SetFieldValueAndRender(existing.Single(f => f.Name == "Nome"), "Anteprima àèìòù", 595, 842);
        using var snapshot = PdfPrintSnapshot.Capture(original);
        Assert.Equal(expected, snapshot.Render(0, 595, 842));
        Assert.Equal(2, filler.ListFields().Count);
        Assert.Equal("Anteprima àèìòù", filler.ListFields().Single(f => f.Name == "Nome").Value);
    }

    [Fact]
    public void Snapshot_IncludesPendingFieldsWithoutAddingThemToOriginal()
    {
        using var original = PdfDocument.OpenFromBytes(PageBytes);
        var changes = new PdfFormChange[]
        {
            new AddFieldChange(0, new PdfFieldSpec("Nuovo", PdfFieldKind.CheckBox,
                new PdfRect(50, 450, 70, 470), CheckedByDefault: true)),
        };
        using var snapshot = PdfPrintSnapshot.Capture(original, changes);
        using var expectedDoc = PdfDocument.OpenFromBytes(PdfFormDesigner.Apply(PageBytes, changes));
        using var expectedFiller = new PdfFormFiller(expectedDoc);
        Assert.Equal(expectedFiller.RenderPageToBgra(0, 595, 842), snapshot.Render(0, 595, 842));
        using var originalFiller = new PdfFormFiller(original);
        Assert.Empty(originalFiller.ListFields());
    }
}
