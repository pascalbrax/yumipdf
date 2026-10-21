using PdfEngine;
using PdfEngine.Annotations;
using Xunit;

namespace PdfEngine.Tests;

public class PdfEditableAnnotationsTests
{
    private const int W = 595, H = 842; // text-page.pdf at 1 px per point

    private static string FixturePath(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    private static PdfDocument OpenFixture() => PdfDocument.OpenFromPath(FixturePath("text-page.pdf"));

    private static byte[] Render(PdfDocument document)
    {
        using var page = document.GetPage(0);
        return page.RenderToBgra(W, H);
    }

    private static T WithPage<T>(PdfDocument document, Func<PdfPage, T> action)
    {
        using var page = document.GetPage(0);
        return action(page);
    }

    /// <summary>Bounding box, in page points (bottom-left origin), of every pixel that differs between two renders; null if none.</summary>
    private static PdfRect? ChangedBox(byte[] before, byte[] after)
    {
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        for (var y = 0; y < H; y++)
        {
            for (var x = 0; x < W; x++)
            {
                var o = (y * W + x) * 4;
                if (before[o] != after[o] || before[o + 1] != after[o + 1] || before[o + 2] != after[o + 2])
                {
                    minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                    minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
                }
            }
        }

        return maxX < 0 ? null : new PdfRect(minX, H - 1 - maxY, maxX + 1, H - minY);
    }

    private static (byte B, byte G, byte R) PixelAtPoint(byte[] bgra, double xPt, double yPt)
    {
        var o = ((H - 1 - (int)yPt) * W + (int)xPt) * 4;
        return (bgra[o], bgra[o + 1], bgra[o + 2]);
    }

    private static PdfDocument SaveAndReload(PdfDocument document)
    {
        var path = Path.Combine(Path.GetTempPath(), $"pdf26-annot-{Guid.NewGuid():N}.pdf");
        try
        {
            document.SaveAs(path);
            return PdfDocument.OpenFromBytes(File.ReadAllBytes(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AddNote_DrawsAnIconAndListsItsText()
    {
        using var document = OpenFixture();
        var before = Render(document);

        var index = WithPage(document, p => PdfEditableAnnotations.AddNote(p, 300, 500, "Da rivedere"));

        var note = Assert.Single(WithPage(document, PdfEditableAnnotations.List));
        Assert.Equal(index, note.Index);
        Assert.Equal(PdfAnnotationKind.Note, note.Kind);
        Assert.Equal("Da rivedere", note.Contents);
        Assert.Equal(PdfEditableAnnotations.NoteSize, note.Rect.Width, 1);
        Assert.NotNull(ChangedBox(before, Render(document)));
    }

    [Fact]
    public void AddNote_SurvivesSaveAndReload()
    {
        using var document = OpenFixture();
        WithPage(document, p => PdfEditableAnnotations.AddNote(p, 300, 500, "Perché è così? città"));

        using var reloaded = SaveAndReload(document);

        var note = Assert.Single(WithPage(reloaded, PdfEditableAnnotations.List));
        Assert.Equal("Perché è così? città", note.Contents);
        Assert.Equal(PdfAnnotationKind.Note, note.Kind);
    }

    [Fact]
    public void SetContents_ReplacesTheNoteText()
    {
        using var document = OpenFixture();
        var index = WithPage(document, p => PdfEditableAnnotations.AddNote(p, 300, 500, "uno"));

        WithPage(document, p => { PdfEditableAnnotations.SetContents(p, index, "due"); return 0; });

        Assert.Equal("due", Assert.Single(WithPage(document, PdfEditableAnnotations.List)).Contents);
    }

    [Fact]
    public void Remove_LeavesThePageExactlyAsItWas()
    {
        using var document = OpenFixture();
        var before = Render(document);
        var note = WithPage(document, p => PdfEditableAnnotations.AddNote(p, 300, 500, "x"));
        var stamp = WithPage(document, p => PdfEditableAnnotations.AddTextStamp(p, 200, 300, "BOZZA", new PdfColor(0, 0, 200)));

        WithPage(document, p => { PdfEditableAnnotations.Remove(p, stamp); PdfEditableAnnotations.Remove(p, note); return 0; });

        Assert.Empty(WithPage(document, PdfEditableAnnotations.List));
        Assert.Null(ChangedBox(before, Render(document)));
    }

    [Fact]
    public void AddTextStamp_DrawsOnlyInsideItsRectangle()
    {
        using var document = OpenFixture();
        var before = Render(document);

        WithPage(document, p => PdfEditableAnnotations.AddTextStamp(p, 300, 400, "APPROVATO", new PdfColor(0, 128, 0)));

        var stamp = Assert.Single(WithPage(document, PdfEditableAnnotations.List));
        Assert.Equal(PdfAnnotationKind.Stamp, stamp.Kind);
        Assert.InRange(stamp.Rect.Left + stamp.Rect.Width / 2, 299, 301);
        Assert.InRange(stamp.Rect.Bottom + stamp.Rect.Height / 2, 399, 401);
        var changed = ChangedBox(before, Render(document));
        Assert.NotNull(changed);
        Assert.True(changed.Value.Left >= stamp.Rect.Left - 1 && changed.Value.Right <= stamp.Rect.Right + 1, "drawn wider than the rectangle");
        Assert.True(changed.Value.Bottom >= stamp.Rect.Bottom - 1 && changed.Value.Top <= stamp.Rect.Top + 1, "drawn taller than the rectangle");
    }

    [Fact]
    public void AddTextStamp_WithSubtitle_IsTallerThanWithout()
    {
        using var document = OpenFixture();
        WithPage(document, p => PdfEditableAnnotations.AddTextStamp(p, 300, 400, "APPROVATO", new PdfColor(0, 128, 0)));
        WithPage(document, p => PdfEditableAnnotations.AddTextStamp(p, 300, 600, "APPROVATO", new PdfColor(0, 128, 0), "07.10.2026"));

        var stamps = WithPage(document, PdfEditableAnnotations.List);

        Assert.Equal(2, stamps.Count);
        Assert.True(stamps[1].Rect.Height > stamps[0].Rect.Height + 5);
    }

    [Fact]
    public void AddImageStamp_ShowsTheImage()
    {
        using var document = OpenFixture();
        // 10x5 opaque red (BGRA) - longer side scaled to 100 pt, so it covers 100x50 pt around (300, 400).
        var pixels = new byte[10 * 5 * 4];
        for (var i = 0; i < pixels.Length; i += 4) { pixels[i] = 0; pixels[i + 1] = 0; pixels[i + 2] = 255; pixels[i + 3] = 255; }

        WithPage(document, p => PdfEditableAnnotations.AddImageStamp(p, 300, 400, pixels, 10, 5, longSidePt: 100));

        var stamp = Assert.Single(WithPage(document, PdfEditableAnnotations.List));
        Assert.Equal(100, stamp.Rect.Width, 1);
        Assert.Equal(50, stamp.Rect.Height, 1);
        Assert.Equal((0, 0, 255), PixelAtPoint(Render(document), 300, 400));
    }

    [Fact]
    public void AddImageStamp_KeepsTransparentPixelsTransparent()
    {
        using var document = OpenFixture();
        var pixels = new byte[4 * 4 * 4]; // fully transparent
        WithPage(document, p => PdfEditableAnnotations.AddImageStamp(p, 300, 400, pixels, 4, 4, longSidePt: 100));

        Assert.Equal((255, 255, 255), PixelAtPoint(Render(document), 300, 400));
    }

    [Fact]
    public void SetRect_MovesTheStampAndLeavesNothingBehind()
    {
        using var document = OpenFixture();
        var before = Render(document);
        var index = WithPage(document, p => PdfEditableAnnotations.AddTextStamp(p, 300, 400, "COPIA", new PdfColor(200, 0, 0)));
        var placed = WithPage(document, PdfEditableAnnotations.List)[0].Rect;

        WithPage(document, p =>
        {
            PdfEditableAnnotations.SetRect(p, index, new PdfRect(100, 100, 100 + placed.Width, 100 + placed.Height));
            return 0;
        });

        var moved = ChangedBox(before, Render(document));
        Assert.NotNull(moved);
        Assert.True(moved.Value.Top < 100 + placed.Height + 2 && moved.Value.Left > 98, "the stamp is not where it was moved to");
        Assert.True(moved.Value.Right < 100 + placed.Width + 2);
    }

    [Fact]
    public void Add_NearTheEdge_StaysInsideThePage()
    {
        using var document = OpenFixture();

        WithPage(document, p => PdfEditableAnnotations.AddNote(p, -50, 2000, "fuori"));
        WithPage(document, p => PdfEditableAnnotations.AddTextStamp(p, 590, 5, "COPIA", new PdfColor(0, 0, 0)));

        foreach (var annotation in WithPage(document, PdfEditableAnnotations.List))
        {
            Assert.True(annotation.Rect.Left >= 0 && annotation.Rect.Bottom >= 0, $"{annotation.Kind} outside bottom-left");
            Assert.True(annotation.Rect.Right <= W + 0.01 && annotation.Rect.Top <= H + 0.01, $"{annotation.Kind} outside top-right");
        }
    }

    [Fact]
    public void Stamp_SurvivesSaveAndReload_WithItsAppearance()
    {
        using var document = OpenFixture();
        WithPage(document, p => PdfEditableAnnotations.AddTextStamp(p, 300, 400, "RIFIUTATO", new PdfColor(200, 0, 0), "07.10.2026"));
        var drawn = Render(document);

        using var reloaded = SaveAndReload(document);

        Assert.Equal(PdfAnnotationKind.Stamp, Assert.Single(WithPage(reloaded, PdfEditableAnnotations.List)).Kind);
        Assert.Null(ChangedBox(drawn, Render(reloaded)));
    }

    [Fact]
    public void List_IgnoresOtherKindsOfAnnotation()
    {
        using var document = OpenFixture();
        WithPage(document, p => { PdfAnnotationService.AddHighlight(p, (100, 600, 200, 620), new PdfColor(255, 255, 0, 128)); return 0; });
        WithPage(document, p => PdfEditableAnnotations.AddNote(p, 300, 500, "n"));

        var listed = WithPage(document, PdfEditableAnnotations.List);

        var note = Assert.Single(listed);
        Assert.Equal(PdfAnnotationKind.Note, note.Kind);
        Assert.Equal(1, note.Index); // the highlight is annotation 0: indices are the page's, not the filtered list's
    }

    [Theory]
    [InlineData(2.0)]   // contains the old rectangle: the case PDFium used to answer by not scaling at all
    [InlineData(1.2)]
    [InlineData(0.5)]   // shrinking
    public void SetRect_ResizingAStamp_ScalesItsPicture(double factor)
    {
        using var document = OpenFixture();
        WithPage(document, p => PdfEditableAnnotations.AddTextStamp(p, 300, 400, "APPROVATO", new PdfColor(0, 128, 0)));
        var placed = WithPage(document, PdfEditableAnnotations.List)[0].Rect;
        var resized = new PdfRect(placed.Left, placed.Top - placed.Height * factor, placed.Left + placed.Width * factor, placed.Top);
        var blank = Render(OpenFixture());

        WithPage(document, p => { PdfEditableAnnotations.SetRect(p, 0, resized); return 0; });

        // What is drawn now (compared with the page without the stamp) fills the new rectangle, not the old one.
        var drawn = ChangedBox(blank, Render(document));
        Assert.NotNull(drawn);
        Assert.InRange(drawn.Value.Right, resized.Right - 3, resized.Right + 1);
        Assert.InRange(drawn.Value.Left, resized.Left - 1, resized.Left + 3);
        Assert.InRange(drawn.Value.Bottom, resized.Bottom - 1, resized.Bottom + 3);
    }

    [Fact]
    public void SetRect_ResizingAfterSaveAndReload_StillScales()
    {
        using var document = OpenFixture();
        WithPage(document, p => PdfEditableAnnotations.AddTextStamp(p, 300, 400, "BOZZA", new PdfColor(0, 90, 200)));
        using var reloaded = SaveAndReload(document);
        var stamp = Assert.Single(WithPage(reloaded, PdfEditableAnnotations.List));
        Assert.True(stamp.CanResize);
        var bigger = new PdfRect(stamp.Rect.Left, stamp.Rect.Top - stamp.Rect.Height * 3, stamp.Rect.Left + stamp.Rect.Width * 3, stamp.Rect.Top);
        var blank = Render(OpenFixture());

        WithPage(reloaded, p => { PdfEditableAnnotations.SetRect(p, stamp.Index, bigger); return 0; });

        var drawn = ChangedBox(blank, Render(reloaded));
        Assert.NotNull(drawn);
        Assert.InRange(drawn.Value.Right, bigger.Right - 4, bigger.Right + 1);
    }

    [Fact]
    public void SetRect_MovingAndEnlargingAnImageStamp_ScalesTheImage()
    {
        using var document = OpenFixture();
        var pixels = new byte[10 * 5 * 4];
        for (var i = 0; i < pixels.Length; i += 4) { pixels[i] = 0; pixels[i + 1] = 0; pixels[i + 2] = 255; pixels[i + 3] = 255; }
        var index = WithPage(document, p => PdfEditableAnnotations.AddImageStamp(p, 300, 400, pixels, 10, 5, longSidePt: 100));

        WithPage(document, p => { PdfEditableAnnotations.SetRect(p, index, new PdfRect(100, 100, 400, 250)); return 0; });

        Assert.Equal((0, 0, 255), PixelAtPoint(Render(document), 390, 240)); // far corner of the enlarged rectangle
        Assert.Equal((255, 255, 255), PixelAtPoint(Render(document), 300, 400)); // where it used to be
    }

    [Fact]
    public void CanResize_IsTrueForOurStampsAndFalseForNotes()
    {
        using var document = OpenFixture();
        WithPage(document, p => PdfEditableAnnotations.AddNote(p, 300, 500, "n"));
        WithPage(document, p => PdfEditableAnnotations.AddTextStamp(p, 300, 300, "COPIA", new PdfColor(0, 0, 0)));

        var list = WithPage(document, PdfEditableAnnotations.List);

        Assert.False(list.Single(a => a.Kind == PdfAnnotationKind.Note).CanResize);
        Assert.True(list.Single(a => a.Kind == PdfAnnotationKind.Stamp).CanResize);
    }

    [Fact]
    public void An_annotation_identity_is_given_once_and_follows_the_annotation_when_others_are_removed()
    {
        using var document = OpenFixture();
        using var page = document.GetPage(0);
        var first = PdfEditableAnnotations.AddNote(page, 100, 100, "uno");
        var second = PdfEditableAnnotations.AddNote(page, 300, 300, "due");
        Assert.All(PdfEditableAnnotations.List(page), a => Assert.Null(a.Id));

        var id = PdfEditableAnnotations.EnsureId(page, second);
        Assert.Equal(id, PdfEditableAnnotations.EnsureId(page, second)); // asked again: the same one
        Assert.Equal(id, PdfEditableAnnotations.List(page).Single(a => a.Index == second).Id);
        Assert.Equal(second, PdfEditableAnnotations.IndexOfId(page, id));

        PdfEditableAnnotations.Remove(page, first); // the second one moves up by one
        Assert.Equal(second - 1, PdfEditableAnnotations.IndexOfId(page, id));
        Assert.Equal(-1, PdfEditableAnnotations.IndexOfId(page, "inesistente"));
    }

    [Fact]
    public void An_annotation_can_be_put_back_under_its_old_identity()
    {
        using var document = OpenFixture();
        using var page = document.GetPage(0);
        var index = PdfEditableAnnotations.AddNote(page, 120, 220, "testo");
        var id = PdfEditableAnnotations.EnsureId(page, index);

        PdfEditableAnnotations.Remove(page, index);
        Assert.Equal(-1, PdfEditableAnnotations.IndexOfId(page, id));

        var again = PdfEditableAnnotations.AddNote(page, 120, 220, "testo");
        PdfEditableAnnotations.SetId(page, again, id);
        Assert.Equal(again, PdfEditableAnnotations.IndexOfId(page, id));

        // The identity is written into the file: it survives saving and reopening.
        using var reopened = PdfDocument.OpenFromBytes(document.SaveToBytes());
        using var reopenedPage = reopened.GetPage(0);
        Assert.Equal(id, PdfEditableAnnotations.List(reopenedPage).Single().Id);
    }
}
