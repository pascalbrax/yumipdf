using PdfEngine.Editing;
using Xunit;

namespace PdfEngine.Tests;

public class FallbackFontsTests
{
    [Theory]
    [InlineData("PDF26Sans", "Sans")]
    [InlineData("PDF26Serif", "Serif")]
    [InlineData("PDF26Mono", "Mono")]
    public void Every_bundled_style_loads_and_describes_its_own_family(string family, string expectedName)
    {
        var expected = Enum.Parse<FallbackFamily>(expectedName);
        foreach (var style in new[] { "Regular", "Bold", "Italic", "BoldItalic" })
        {
            var look = SfntInfo.Read(FallbackFonts.Load($"{family}-{style}"));
            Assert.Equal(expected, look.Family);
            Assert.Equal(style.Contains("Bold"), look.Bold == true);
            Assert.Equal(style.Contains("Italic"), look.Italic == true);
        }
    }

    [Fact]
    public void Noto_Sans_loads_in_all_four_styles()
    {
        foreach (var style in new[] { "Regular", "Bold", "Italic", "BoldItalic" })
        {
            var look = SfntInfo.Read(FallbackFonts.Load($"NotoSans-{style}"));
            Assert.Equal(FallbackFamily.Sans, look.Family);
            Assert.Equal(style.Contains("Bold"), look.Bold == true);
            Assert.Equal(style.Contains("Italic"), look.Italic == true);
        }
    }

    [Fact]
    public void The_font_licenses_ship_with_the_fonts()
    {
        var assembly = typeof(FallbackFonts).Assembly;
        foreach (var name in new[] { "LICENSE-Liberation.txt", "LICENSE-NotoSans.txt", "AUTHORS-Liberation.txt" })
        {
            using var stream = assembly.GetManifestResourceStream($"PdfEngine.Fonts.{name}");
            Assert.NotNull(stream);
        }
    }

    [Fact]
    public void Garbage_in_a_font_file_is_no_look_rather_than_a_crash()
    {
        Assert.Equal(default, SfntInfo.Read(new byte[] { 1, 2, 3 }));
        Assert.Equal(default, SfntInfo.Read(new byte[200]));
        var truncated = FallbackFonts.Load("PDF26Sans-Regular").Take(300).ToArray();
        _ = SfntInfo.Read(truncated); // must not throw
    }

    [Theory]
    [InlineData("ArialMT", 0, "Sans", false, false)]
    [InlineData("Arial-BoldMT", 32, "Sans", true, false)]
    [InlineData("TimesNewRomanPS-BoldItalicMT", 0, "Serif", true, true)]
    [InlineData("Courier-Oblique", 0, "Mono", false, true)]
    [InlineData("ABCDEF+Georgia", 0, "Serif", false, false)]
    [InlineData("Helvetica", 0, "Sans", false, false)]
    public void A_font_name_alone_picks_a_similar_fallback(string name, int flags, string family, bool bold, bool italic)
        => Assert.Equal((Enum.Parse<FallbackFamily>(family), bold, italic), FallbackFonts.Classify(name, flags, 0, 0));

    [Fact]
    public void The_font_files_own_style_beats_careless_descriptor_flags_and_names()
    {
        // As measured on a real document: an Arial-like bold sans font named "CIDFont+F2" whose descriptor claims serif + symbolic.
        var look = new FontLook(FallbackFamily.Sans, Bold: true, Italic: null);
        Assert.Equal((FallbackFamily.Sans, true, false), FallbackFonts.Classify("CIDFont+F2", 6, 3696, 0, look));
        // Without the file to ask, the flags are all there is.
        Assert.Equal((FallbackFamily.Sans, false, false), FallbackFonts.Classify("CIDFont+F2", 6, 3696, 0));
    }

    [Fact]
    public void The_chain_prefers_the_matching_family_then_Noto_Sans_in_the_same_style()
    {
        var chain = FallbackFonts.Chain(FallbackFamily.Serif, bold: true, italic: false);
        Assert.Equal(new[] { "PDF26Serif-Bold", "NotoSans-Bold" }, chain.Select(c => c.Key));
        Assert.Equal(new[] { "PDF26Mono-BoldItalic", "NotoSans-BoldItalic" }, FallbackFonts.Chain(FallbackFamily.Mono, true, true).Select(c => c.Key));
    }
}
