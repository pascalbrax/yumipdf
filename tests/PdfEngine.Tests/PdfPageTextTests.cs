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
    public void FindTextRects_OnATextPage_FindsTheWord()
    {
        using var document = PdfDocument.OpenFromPath(FixturePath("text-page.pdf"));
        using var page = document.GetPage(0);

        var rects = page.FindTextRects("mondo");

        Assert.Single(rects);
        var (left, bottom, right, top) = rects[0];
        Assert.True(right > left);
        Assert.True(top > bottom);
    }

    [Fact]
    public void FindTextRects_IsCaseInsensitive()
    {
        using var document = PdfDocument.OpenFromPath(FixturePath("text-page.pdf"));
        using var page = document.GetPage(0);

        Assert.Single(page.FindTextRects("MONDO"));
    }

    [Fact]
    public void FindTextRects_WithNoMatch_ReturnsEmpty()
    {
        using var document = PdfDocument.OpenFromPath(FixturePath("text-page.pdf"));
        using var page = document.GetPage(0);

        Assert.Empty(page.FindTextRects("assente"));
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
