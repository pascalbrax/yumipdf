using PdfEngine;
using Xunit;

namespace PdfEngine.Tests;

/// <summary>
/// <see cref="PdfPage.HasExtractableText"/>/<see cref="PdfPage.ExtractText"/> - the "does this
/// page already have text, or is it a scan" check the OCR pipeline gates on
/// (<c>MainViewModel.BeginOcr</c>, App project).
/// </summary>
public class PdfPageTextTests
{
    [Fact]
    public void HasExtractableText_OnATextPage_IsTrue()
    {
        using var document = PdfDocument.OpenFromPath(FixturePath("text-page.pdf"));
        using var page = document.GetPage(0);

        Assert.True(page.HasExtractableText());
        Assert.Equal("Ciao mondo", page.ExtractText());
    }

    [Fact]
    public void HasExtractableText_OnAnImageOnlyPage_IsFalse()
    {
        using var document = PdfDocument.OpenFromPath(FixturePath("image-only-page.pdf"));
        using var page = document.GetPage(0);

        Assert.False(page.HasExtractableText());
        Assert.Equal(string.Empty, page.ExtractText());
    }

    private static string FixturePath(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
}
