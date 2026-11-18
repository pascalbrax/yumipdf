using PdfEngine;
using PdfEngine.Annotations;
using PdfEngine.Editing;
using PdfEngine.Organize;
using Xunit;

namespace PdfEngine.Tests;

public class PdfTextInsertTests
{
    private static readonly PdfColor Black = new(0, 0, 0);

    /// <summary>One blank page, 300 x 200 points.</summary>
    private static byte[] BlankPage(int rotation = 0)
    {
        var text = "%PDF-1.4\n1 0 obj<</Type/Catalog/Pages 2 0 R>>endobj\n"
                   + "2 0 obj<</Type/Pages/Count 1/Kids[3 0 R]>>endobj\n"
                   + "3 0 obj<</Type/Page/Parent 2 0 R/MediaBox[0 0 300 200]>>endobj\n"
                   + "trailer<</Root 1 0 R/Size 4>>\n%%EOF\n";
        var bytes = System.Text.Encoding.ASCII.GetBytes(text);
        return rotation == 0 ? bytes : PdfPageOrganizer.Apply(bytes, new[] { 0 }, quarterTurns: new[] { rotation });
    }

    private static PdfTextInsertResult Insert(PdfDocument document, double left, double top, string text, double size = 20)
    {
        using var page = document.GetPage(0);
        return PdfTextEditing.Insert(page, left, top, text, size, Black);
    }

    /// <summary>The box of the dark pixels of page 1 as it is shown (one pixel per point): left, top (row from the top), right, bottom.</summary>
    private static (int Left, int Top, int Right, int Bottom)? InkBox(PdfDocument document)
    {
        using var page = document.GetPage(0);
        var (width, height) = page.SizeInPoints;
        var w = (int)Math.Round(width);
        var h = (int)Math.Round(height);
        var bgra = page.RenderToBgra(w, h);
        int left = int.MaxValue, top = int.MaxValue, right = -1, bottom = -1;
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var i = (y * w + x) * 4;
                if (bgra[i] + bgra[i + 1] + bgra[i + 2] < 300)
                {
                    left = Math.Min(left, x);
                    right = Math.Max(right, x);
                    top = Math.Min(top, y);
                    bottom = Math.Max(bottom, y);
                }
            }
        }

        return right < 0 ? null : (left, top, right, bottom);
    }

    private static int LineCount(PdfDocument document)
    {
        using var page = document.GetPage(0);
        return PdfTextEditing.List(page).Count;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Insert_PutsTheTopLeftOfTheTextWhereAskedAndKeepsItUpright(int rotation)
    {
        using var document = PdfDocument.OpenFromBytes(BlankPage(rotation));
        var (shownWidth, shownHeight) = SizeShown(document);

        // 40 points from the left and 50 from the top of the page as it is shown.
        Insert(document, 40, shownHeight - 50, "Hello World");

        var box = InkBox(document) ?? throw new Xunit.Sdk.XunitException("nothing was drawn");
        Assert.InRange(box.Left, 39, 44);
        Assert.InRange(box.Top, 49, 56);
        Assert.True(box.Right - box.Left > 2 * (box.Bottom - box.Top), $"the text is not lying horizontally: {box}"); // upright: wide, not tall
        Assert.True(box.Right < shownWidth);
    }

    private static (double Width, double Height) SizeShown(PdfDocument document)
    {
        using var page = document.GetPage(0);
        return page.SizeInPoints;
    }

    [Fact]
    public void Insert_ListsTheNewLineLikeAnyOther()
    {
        using var document = PdfDocument.OpenFromBytes(BlankPage());

        var result = Insert(document, 30, 120, "Una riga nuova", 14);

        using var page = document.GetPage(0);
        var line = Assert.Single(PdfTextEditing.List(page));
        Assert.Equal("Una riga nuova", line.Text);
        Assert.Equal(result.ObjectIndex, line.ObjectIndex);
        Assert.Equal(14, line.FontSize, 1);
        Assert.Equal(string.Empty, result.MissingCharacters);
    }

    [Fact]
    public void Insert_KeepsAccentedLettersAfterSavingAndReopening()
    {
        using var document = PdfDocument.OpenFromBytes(BlankPage());
        Insert(document, 30, 120, "Città più è già, naïve ß");

        using var reopened = PdfDocument.OpenFromBytes(document.SaveToBytes());
        using var page = reopened.GetPage(0);
        Assert.Contains("Città più è già, naïve ß", page.ExtractText());
    }

    [Fact]
    public void Insert_ReportsCharactersNoFontHas()
    {
        using var document = PdfDocument.OpenFromBytes(BlankPage());

        var result = Insert(document, 30, 120, "ok 中");

        Assert.Contains('中', result.MissingCharacters);
    }

    [Fact]
    public void Insert_UsesTheColorAndTheSize()
    {
        using var document = PdfDocument.OpenFromBytes(BlankPage());
        using (var page = document.GetPage(0))
        {
            PdfTextEditing.Insert(page, 30, 150, "MMMM", 40, new PdfColor(220, 0, 0));
        }

        using var p = document.GetPage(0);
        var bgra = p.RenderToBgra(300, 200);
        var reddish = 0;
        var other = 0;
        for (var i = 0; i < bgra.Length; i += 4)
        {
            if (bgra[i + 2] > 180 && bgra[i + 1] < 80 && bgra[i] < 80)
            {
                reddish++;
            }
            else if (bgra[i + 2] + bgra[i + 1] + bgra[i] < 300)
            {
                other++; // dark and not red
            }
        }

        Assert.True(reddish > 300, $"red pixels: {reddish}");
        Assert.Equal(0, other);
        Assert.Equal(40, PdfTextEditing.List(p).Single().FontSize, 1);
    }

    [Fact]
    public void Insert_CanBeUndoneAndRedoneOnFreshlyLoadedPages()
    {
        using var document = PdfDocument.OpenFromBytes(BlankPage());
        var result = Insert(document, 40, 150, "Hello");
        var drawn = InkBox(document);
        Assert.NotNull(drawn);

        using (var page = document.GetPage(0))
        {
            result.Undo.Undo(page);
        }

        Assert.Equal(0, LineCount(document));
        Assert.Null(InkBox(document));

        using (var page = document.GetPage(0))
        {
            result.Undo.Redo(page);
        }

        Assert.Equal(1, LineCount(document));
        Assert.Equal(drawn, InkBox(document));

        using (var page = document.GetPage(0))
        {
            result.Undo.Undo(page);
        }

        result.Undo.Dispose(); // undone and discarded: the line it held is freed with it
        Assert.Equal(0, LineCount(document));
    }

    [Fact]
    public void Insert_NextToExistingLines_UndoesOnlyTheNewOne()
    {
        using var document = PdfDocument.OpenFromBytes(BlankPage());
        Insert(document, 30, 170, "Prima");
        var second = Insert(document, 30, 120, "Seconda");

        using (var page = document.GetPage(0))
        {
            second.Undo.Undo(page);
        }

        using var p = document.GetPage(0);
        Assert.Equal("Prima", Assert.Single(PdfTextEditing.List(p)).Text);
        second.Undo.Dispose();
    }

    [Fact]
    public void Insert_AnEmptyTextOrASizeThatIsNotPositive_Throws()
    {
        using var document = PdfDocument.OpenFromBytes(BlankPage());
        using var page = document.GetPage(0);

        Assert.Throws<ArgumentException>(() => PdfTextEditing.Insert(page, 10, 100, "  \r\n ", 12, Black));
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfTextEditing.Insert(page, 10, 100, "x", 0, Black));
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfTextEditing.Insert(page, 10, 100, "x", double.NaN, Black));
        Assert.Empty(PdfTextEditing.List(page));
    }

    [Fact]
    public void Insert_TurnsLineBreaksIntoSpaces()
    {
        using var document = PdfDocument.OpenFromBytes(BlankPage());

        Insert(document, 30, 120, "uno\r\ndue\ttre");

        using var page = document.GetPage(0);
        Assert.Equal("uno due tre", Assert.Single(PdfTextEditing.List(page)).Text);
    }

    // ---- Move ----

    private static PdfEditRecord Move(PdfDocument document, int index, double dx, double dy)
    {
        using var page = document.GetPage(0);
        return PdfTextEditing.Move(page, index, dx, dy);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Move_ShiftsTheLineAsShownAndUndoRedoPutItBack(int rotation)
    {
        using var document = PdfDocument.OpenFromBytes(BlankPage(rotation));
        var (_, shownHeight) = SizeShown(document);
        var line = Insert(document, 40, shownHeight - 50, "Hello World");
        var before = InkBox(document) ?? throw new Xunit.Sdk.XunitException("nothing was drawn");

        // 40 points to the right and 30 points down, as shown (y points up, so dy is negative); the text stays inside the page.
        var record = Move(document, line.ObjectIndex, 40, -30);

        var after = InkBox(document) ?? throw new Xunit.Sdk.XunitException("the line disappeared");
        Assert.InRange(after.Left - before.Left, 39, 41);
        Assert.InRange(after.Top - before.Top, 29, 31);
        Assert.InRange(after.Right - before.Right, 39, 41);
        Assert.InRange(after.Bottom - before.Bottom, 29, 31);
        Assert.Equal(1, LineCount(document));

        using (var page = document.GetPage(0))
        {
            record.Undo(page);
        }

        Assert.Equal(before, InkBox(document));

        using (var page = document.GetPage(0))
        {
            record.Redo(page);
        }

        Assert.Equal(after, InkBox(document));
        record.Dispose();
    }

    [Fact]
    public void Move_KeepsTheTextTheIndexAndTheNewPositionAfterSaving()
    {
        using var document = PdfDocument.OpenFromBytes(BlankPage());
        var line = Insert(document, 30, 120, "Spostami");
        Move(document, line.ObjectIndex, 100, -40);

        using var reopened = PdfDocument.OpenFromBytes(document.SaveToBytes());
        using var page = reopened.GetPage(0);
        var moved = Assert.Single(PdfTextEditing.List(page));
        Assert.Equal("Spostami", moved.Text);
        Assert.Equal(line.ObjectIndex, moved.ObjectIndex);
        Assert.InRange(moved.Rect.Left, 129, 134);
    }

    [Fact]
    public void Move_WorksOnALineTheDocumentAlreadyHad_AndLeavesTheOthersWhereTheyAre()
    {
        using var document = PdfDocument.OpenFromBytes(BlankPage());
        Insert(document, 30, 170, "Ferma");
        var other = Insert(document, 30, 100, "Mobile");
        using var reopened = PdfDocument.OpenFromBytes(document.SaveToBytes()); // both lines are now just text the document has

        using var page = reopened.GetPage(0);
        var lines = PdfTextEditing.List(page);
        var fixedBefore = lines.Single(l => l.Text == "Ferma").Rect;
        var mobile = lines.Single(l => l.Text == "Mobile");
        PdfTextEditing.Move(page, mobile.ObjectIndex, 20, 15);

        var after = PdfTextEditing.List(page);
        Assert.Equal(fixedBefore, after.Single(l => l.Text == "Ferma").Rect);
        Assert.Equal(mobile.Rect.Left + 20, after.Single(l => l.Text == "Mobile").Rect.Left, 1);
        Assert.Equal(mobile.Rect.Top + 15, after.Single(l => l.Text == "Mobile").Rect.Top, 1);
        other.Undo.Dispose();
    }

    [Fact]
    public void Move_AnIndexThatIsNotATextLine_OrNotANumber_Throws()
    {
        using var document = PdfDocument.OpenFromBytes(BlankPage());
        var line = Insert(document, 30, 120, "x");
        using var page = document.GetPage(0);

        Assert.Throws<ArgumentOutOfRangeException>(() => PdfTextEditing.Move(page, line.ObjectIndex, double.NaN, 0));
        Assert.ThrowsAny<Exception>(() => PdfTextEditing.Move(page, 99, 1, 1));
    }
}
