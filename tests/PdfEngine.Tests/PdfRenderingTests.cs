using PdfEngine;
using Xunit;

namespace PdfEngine.Tests;

public class PdfRenderingTests
{
    private const int Width = 1007;
    private const int Height = 1426;

    private static byte[] Render(bool lcdText)
    {
        using var document = PdfDocument.OpenFromPath(Path.Combine(AppContext.BaseDirectory, "Fixtures", "text-page.pdf"));
        using var page = document.GetPage(0);
        return page.RenderToBgra(Width, Height, lcdText: lcdText);
    }

    /// <summary>Pixels whose red and blue differ a lot: on black text on white, only LCD (sub-pixel) anti-aliasing makes them.</summary>
    private static int CountColoredPixels(byte[] bgra)
    {
        var count = 0;
        for (var i = 0; i < bgra.Length; i += 4)
        {
            if (Math.Abs(bgra[i + 2] - bgra[i]) > 40)
            {
                count++;
            }
        }

        return count;
    }

    private static int CountDarkPixels(byte[] bgra)
    {
        var count = 0;
        for (var i = 0; i < bgra.Length; i += 4)
        {
            if (bgra[i] + bgra[i + 1] + bgra[i + 2] < 300)
            {
                count++;
            }
        }

        return count;
    }

    [Fact]
    public void RenderToBgra_ByDefault_UsesGrayAntiAliasing()
        => Assert.Equal(0, CountColoredPixels(Render(lcdText: false)));

    [Fact]
    public void RenderToBgra_WithLcdText_UsesSubpixelAntiAliasing()
        => Assert.True(CountColoredPixels(Render(lcdText: true)) > 100);

    [Fact]
    public void RenderToBgra_WithLcdText_ReturnsAnOpaqueBuffer()
    {
        var bgra = Render(lcdText: true);

        for (var i = 3; i < bgra.Length; i += 4)
        {
            Assert.Equal(255, bgra[i]);
        }
    }

    [Fact]
    public void RenderToBgra_BothWays_DrawTheSameText()
    {
        var gray = CountDarkPixels(Render(lcdText: false));
        var lcd = CountDarkPixels(Render(lcdText: true));

        // The same glyphs in the same places: the amount of dark ink is about the same.
        Assert.InRange(lcd, gray * 0.85, gray * 1.15);
    }
}
