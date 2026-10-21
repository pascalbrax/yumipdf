using PdfEngine;
using PdfEngine.Annotations;
using PdfEngine.Editing;
using Xunit;

namespace PdfEngine.Tests;

/// <summary>
/// Fixtures/subset-font.pdf (make_subset_font_pdf.py): "Ciao mondo" and "Seconda riga" in an embedded
/// <em>subset</em> font (only the glyphs of those two texts), "Riga standard" in non-embedded Helvetica.
/// </summary>
public class PdfTextEditingTests
{
    private const int W = 595, H = 842;

    private static string FixturePath => Path.Combine(AppContext.BaseDirectory, "Fixtures", "subset-font.pdf");

    private static PdfDocument Open() => PdfDocument.OpenFromPath(FixturePath);

    private static IReadOnlyList<PdfTextLine> Lines(PdfDocument document)
    {
        using var page = document.GetPage(0);
        return PdfTextEditing.List(page);
    }

    private static PdfTextLine Line(PdfDocument document, string text) => Lines(document).Single(l => l.Text == text);

    private static byte[] Render(PdfDocument document)
    {
        using var page = document.GetPage(0);
        return page.RenderToBgra(W, H);
    }

    /// <summary>Number of non-white pixels inside a page-point rectangle (origin bottom-left) of a W x H render.</summary>
    private static int Ink(byte[] bgra, PdfRect rect)
    {
        var count = 0;
        var top = (int)Math.Floor(H - rect.Top);
        var bottom = (int)Math.Ceiling(H - rect.Bottom);
        for (var y = Math.Max(0, top); y < Math.Min(H, bottom); y++)
        {
            for (var x = Math.Max(0, (int)rect.Left); x < Math.Min(W, (int)Math.Ceiling(rect.Right)); x++)
            {
                var o = (y * W + x) * 4;
                if (bgra[o] < 200 || bgra[o + 1] < 200 || bgra[o + 2] < 200)
                {
                    count++;
                }
            }
        }

        return count;
    }

    private static PdfDocument Reopen(PdfDocument document) => PdfDocument.OpenFromBytes(document.SaveToBytes());

    [Fact]
    public void Lists_the_text_lines_with_font_and_position()
    {
        using var document = Open();
        var lines = Lines(document);

        Assert.Equal(new[] { "Ciao mondo", "Riga standard", "Seconda riga" }, lines.Select(l => l.Text));
        var first = lines[0];
        Assert.True(first.IsEmbedded);
        Assert.Contains("PDF26Sans", first.FontName);
        Assert.Equal(24, first.FontSize);
        Assert.InRange(first.Rect.Left, 70, 76);
        Assert.InRange(first.Rect.Bottom, 695, 702);
        Assert.False(lines[1].IsEmbedded);
        Assert.Equal("Helvetica", lines[1].FontName);
    }

    [Fact]
    public void Replacing_with_glyphs_the_font_has_edits_in_place_and_keeps_the_original_font()
    {
        using var document = Open();
        var line = Line(document, "Ciao mondo");
        using var page = document.GetPage(0);

        // "Cia" / "Ciao" / spaces: every letter is in the subset.
        var result = PdfTextEditing.Replace(page, line.ObjectIndex, "Ciao Ciao");
        using var undo = result.Undo;

        Assert.False(result.UsedFallbackFont);
        Assert.Equal(string.Empty, result.MissingCharacters);
        var after = PdfTextEditing.List(page).Single(l => l.ObjectIndex == line.ObjectIndex);
        Assert.Equal("Ciao Ciao", after.Text);
        Assert.Contains("PDF26Sans", after.FontName);
        Assert.DoesNotContain("Noto", after.FontName);
    }

    [Fact]
    public void A_glyph_missing_from_the_subset_rewrites_the_line_in_a_bundled_font_that_draws_it()
    {
        using var document = Open();
        var line = Line(document, "Ciao mondo");
        var beforeInk = Ink(Render(document), line.Rect);
        Assert.True(beforeInk > 100);

        using (var page = document.GetPage(0))
        {
            // Q, w, z, x, y are not in the subset: drawn in place they would be empty boxes.
            var result = PdfTextEditing.Replace(page, line.ObjectIndex, "Quick wizard xyz");
            using var undo = result.Undo;

            Assert.True(result.UsedFallbackFont);
            Assert.StartsWith("PDF26Sans", result.FallbackFontName);
            Assert.Equal(string.Empty, result.MissingCharacters);
        }

        var edited = Line(document, "Quick wizard xyz");
        Assert.True(edited.IsEmbedded);
        Assert.StartsWith("PDF26Sans", edited.FontName);
        Assert.Equal(line.FontSize, edited.FontSize);
        // Same place, same size: the new line starts where the old one did.
        Assert.InRange(edited.Rect.Left, line.Rect.Left - 2, line.Rect.Left + 2);
        Assert.InRange(edited.Rect.Top, line.Rect.Top - 3, line.Rect.Top + 3); // the bottom differs: "Q", "y", "z" have descenders

        // It survives saving and reopening, and is drawn (not boxes: a box is the same shape for every missing letter).
        using var reopened = Reopen(document);
        var again = Line(reopened, "Quick wizard xyz");
        var render = Render(reopened);
        Assert.True(Ink(render, again.Rect) > beforeInk);
        Assert.Contains("PDF26Sans", again.FontName);
    }

    [Fact]
    public void The_fallback_font_keeps_the_colour_and_the_drawing_order()
    {
        using var document = Open();
        var line = Line(document, "Seconda riga"); // drawn in red (0.8 0 0) by the fixture
        var redBefore = RedInk(Render(document), line.Rect);
        Assert.True(redBefore > 50);

        using (var page = document.GetPage(0))
        {
            using var undo = PdfTextEditing.Replace(page, line.ObjectIndex, "Quarta zampa").Undo;
        }

        var edited = Line(document, "Quarta zampa");
        Assert.Equal(line.ObjectIndex, edited.ObjectIndex);
        Assert.True(RedInk(Render(document), edited.Rect) > 50);
    }

    private static int RedInk(byte[] bgra, PdfRect rect)
    {
        var count = 0;
        for (var y = Math.Max(0, (int)(H - rect.Top)); y < Math.Min(H, (int)Math.Ceiling(H - rect.Bottom)); y++)
        {
            for (var x = Math.Max(0, (int)rect.Left); x < Math.Min(W, (int)Math.Ceiling(rect.Right)); x++)
            {
                var o = (y * W + x) * 4;
                if (bgra[o + 2] > 150 && bgra[o + 1] < 90 && bgra[o] < 90)
                {
                    count++;
                }
            }
        }

        return count;
    }

    [Fact]
    public void Cyrillic_and_Greek_on_a_standard_font_line_use_the_fallback_font()
    {
        using var document = Open();
        var line = Line(document, "Riga standard");
        using var page = document.GetPage(0);

        var result = PdfTextEditing.Replace(page, line.ObjectIndex, "Привет Ελλάδα");
        using var undo = result.Undo;

        Assert.True(result.UsedFallbackFont);
        Assert.Equal(string.Empty, result.MissingCharacters);
        Assert.Equal("Привет Ελλάδα", PdfTextEditing.List(page).Single(l => l.ObjectIndex == line.ObjectIndex).Text);
    }

    [Fact]
    public void Accents_a_standard_font_can_draw_stay_in_the_original_font()
    {
        using var document = Open();
        var line = Line(document, "Riga standard");
        using var page = document.GetPage(0);

        var result = PdfTextEditing.Replace(page, line.ObjectIndex, "Città perché è così");
        using var undo = result.Undo;

        Assert.False(result.UsedFallbackFont);
        Assert.Equal("Helvetica", PdfTextEditing.List(page).Single(l => l.ObjectIndex == line.ObjectIndex).FontName);
    }

    [Fact]
    public void Characters_no_bundled_font_has_are_reported()
    {
        using var document = Open();
        var line = Line(document, "Ciao mondo");
        using var page = document.GetPage(0);

        var result = PdfTextEditing.Replace(page, line.ObjectIndex, "Ciao 日本語");
        using var undo = result.Undo;

        Assert.True(result.UsedFallbackFont);
        Assert.Equal(3, result.MissingCharacters.Length);
        Assert.All("日本語", c => Assert.Contains(c, result.MissingCharacters));
    }

    [Fact]
    public void Undo_and_redo_of_an_in_place_edit()
    {
        using var document = Open();
        var line = Line(document, "Ciao mondo");
        var original = Render(document);

        using var page = document.GetPage(0);
        using var record = PdfTextEditing.Replace(page, line.ObjectIndex, "Ciao Ciao").Undo;
        record.Undo(page);
        Assert.Equal("Ciao mondo", PdfTextEditing.List(page).Single(l => l.ObjectIndex == line.ObjectIndex).Text);
        record.Redo(page);
        Assert.Equal("Ciao Ciao", PdfTextEditing.List(page).Single(l => l.ObjectIndex == line.ObjectIndex).Text);
        record.Undo(page);
        Assert.Equal("Ciao mondo", PdfTextEditing.List(page).Single(l => l.ObjectIndex == line.ObjectIndex).Text);

        Assert.Equal(original, Render(document));
    }

    [Fact]
    public void Undo_and_redo_of_a_fallback_font_rewrite_restores_the_original_line_exactly()
    {
        using var document = Open();
        var line = Line(document, "Ciao mondo");
        var original = Render(document);

        using var page = document.GetPage(0);
        using var record = PdfTextEditing.Replace(page, line.ObjectIndex, "Quick wizard").Undo;
        Assert.NotEqual(original, Render(document));

        record.Undo(page);
        Assert.Equal("Ciao mondo", PdfTextEditing.List(page).Single(l => l.ObjectIndex == line.ObjectIndex).Text);
        Assert.Equal(original, Render(document));

        record.Redo(page);
        Assert.Equal("Quick wizard", PdfTextEditing.List(page).Single(l => l.ObjectIndex == line.ObjectIndex).Text);

        record.Undo(page);
        Assert.Equal(original, Render(document));
    }

    [Fact]
    public void Removing_a_line_takes_it_out_of_the_file_and_undo_puts_it_back()
    {
        using var document = Open();
        var line = Line(document, "Riga standard");
        var original = Render(document);

        using var page = document.GetPage(0);
        using var record = PdfTextEditing.Remove(page, line.ObjectIndex);
        Assert.DoesNotContain(PdfTextEditing.List(page), l => l.Text == "Riga standard");
        Assert.Equal(2, PdfTextEditing.List(page).Count);

        // Gone from the saved file too - not merely hidden (search and copy would still find hidden text).
        using (var reopened = Reopen(document))
        {
            using var reopenedPage = reopened.GetPage(0);
            Assert.DoesNotContain("Riga standard", reopenedPage.ExtractText());
            Assert.Contains("Ciao mondo", reopenedPage.ExtractText());
        }

        record.Undo(page);
        Assert.Contains(PdfTextEditing.List(page), l => l.Text == "Riga standard");
        Assert.Equal(original, Render(document));

        record.Redo(page);
        Assert.DoesNotContain(PdfTextEditing.List(page), l => l.Text == "Riga standard");
    }

    [Fact]
    public void An_empty_replacement_and_a_bad_index_are_rejected()
    {
        using var document = Open();
        using var page = document.GetPage(0);
        Assert.Throws<ArgumentException>(() => PdfTextEditing.Replace(page, 0, "   "));
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfTextEditing.Replace(page, 99, "x"));
    }

    [Fact]
    public void Edits_can_be_discarded_without_leaking_or_crashing_when_the_document_closes_first()
    {
        PdfEditRecord record;
        using (var document = Open())
        {
            using var page = document.GetPage(0);
            var line = Lines(document)[0];
            record = PdfTextEditing.Replace(page, line.ObjectIndex, "Quick wizard").Undo;
            record.Undo(page); // the rewritten object now sits off the page, held by the record
        }

        record.Dispose(); // the document (which already freed it) is gone: nothing left to do, nothing to crash
    }
}
