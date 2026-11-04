using PdfEngine;
using PdfEngine.Organize;
using Xunit;

namespace PdfEngine.Tests;

public class PdfPageOrganizerTests
{
    private static string FixturePath(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    // bookmarks.pdf has four pages (0-3) and an outline whose entries point at them:
    // "Capitolo 1" -> page 0, "Sezione 1.1" -> 1, "Sezione 1.2" -> 2, "Capitolo 2" -> 3, "Città e più" (a child of Capitolo 2) -> ?
    private static PdfDocument OpenBookmarks() => PdfDocument.OpenFromPath(FixturePath("bookmarks.pdf"));

    private static int PageOf(PdfDocument document, string title)
    {
        PdfBookmark? Find(IEnumerable<PdfBookmark> nodes)
        {
            foreach (var node in nodes)
            {
                if (node.Title == title)
                {
                    return node;
                }

                if (Find(node.Children) is { } child)
                {
                    return child;
                }
            }

            return null;
        }

        return Find(document.GetBookmarks())!.PageIndex;
    }

    [Fact]
    public void Apply_ReordersThePagesAndTheOutlineFollowsThem()
    {
        using var document = OpenBookmarks();

        PdfPageOrganizer.Apply(document, new[] { 3, 2, 1, 0 });

        Assert.Equal(4, document.PageCount);
        Assert.Equal(3, PageOf(document, "Capitolo 1"));    // was page 0, now last
        Assert.Equal(2, PageOf(document, "Sezione 1.1"));   // was 1
        Assert.Equal(1, PageOf(document, "Sezione 1.2"));   // was 2
        Assert.Equal(0, PageOf(document, "Capitolo 2"));    // was 3, now first
    }

    [Fact]
    public void Apply_RemovesThePagesThatAreNotListed()
    {
        using var document = OpenBookmarks();

        PdfPageOrganizer.Apply(document, new[] { 0, 3 });

        Assert.Equal(2, document.PageCount);
        Assert.Equal(0, PageOf(document, "Capitolo 1"));
        Assert.Equal(1, PageOf(document, "Capitolo 2"));
        Assert.Equal(-1, PageOf(document, "Sezione 1.1")); // pointed at a removed page
    }

    [Fact]
    public void Apply_CanRemoveAndReorderAtTheSameTime()
    {
        using var document = OpenBookmarks();

        PdfPageOrganizer.Apply(document, new[] { 3, 1 }); // keep two pages, swapped

        Assert.Equal(2, document.PageCount);
        Assert.Equal(0, PageOf(document, "Capitolo 2"));
        Assert.Equal(1, PageOf(document, "Sezione 1.1"));
        Assert.Equal(-1, PageOf(document, "Capitolo 1"));
    }

    [Fact]
    public void Apply_WithTheCurrentOrder_ChangesNothing()
    {
        using var document = OpenBookmarks();

        PdfPageOrganizer.Apply(document, new[] { 0, 1, 2, 3 });

        Assert.Equal(new[] { 0, 1, 2, 3 }, new[] { "Capitolo 1", "Sezione 1.1", "Sezione 1.2", "Capitolo 2" }.Select(title => PageOf(document, title)));
    }

    [Fact]
    public void Apply_KeepsTheOutlineWhenTheFileIsSavedAndReopened()
    {
        var original = File.ReadAllBytes(FixturePath("bookmarks.pdf"));

        var rewritten = PdfPageOrganizer.Apply(original, new[] { 2, 0, 1, 3 });

        using var reopened = PdfDocument.OpenFromBytes(rewritten);
        Assert.Equal(4, reopened.PageCount);
        Assert.Equal(new[] { "Capitolo 1", "Capitolo 2", "Sito web" }, reopened.GetBookmarks().Select(b => b.Title));
        Assert.Equal(1, PageOf(reopened, "Capitolo 1")); // page 0 moved to position 1
        Assert.Equal(0, PageOf(reopened, "Sezione 1.2")); // page 2 moved to the front
    }

    [Fact]
    public void Apply_ToBytes_LeavesTheInputUntouched()
    {
        var original = File.ReadAllBytes(FixturePath("bookmarks.pdf"));
        var copy = (byte[])original.Clone();

        PdfPageOrganizer.Apply(original, new[] { 1 });

        Assert.Equal(copy, original);
    }

    [Fact]
    public void Apply_WithNoPages_Throws()
    {
        using var document = OpenBookmarks();

        Assert.Throws<ArgumentException>(() => PdfPageOrganizer.Apply(document, Array.Empty<int>()));
        Assert.Equal(4, document.PageCount);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(4)]
    public void Apply_WithAPageThatDoesNotExist_ThrowsAndChangesNothing(int index)
    {
        using var document = OpenBookmarks();

        Assert.Throws<ArgumentException>(() => PdfPageOrganizer.Apply(document, new[] { 0, index }));
        Assert.Equal(4, document.PageCount);
    }

    private static (double Width, double Height) SizeOf(PdfDocument document, int index)
    {
        using var page = document.GetPage(index);
        return page.SizeInPoints;
    }

    /// <summary>A three-page file whose pages are all different (and none square), so a quarter turn shows as width and height swapped.</summary>
    private static byte[] ThreeShapes()
    {
        var sizes = new[] { (100, 200), (300, 100), (150, 400) };
        var text = new System.Text.StringBuilder("%PDF-1.4\n1 0 obj<</Type/Catalog/Pages 2 0 R>>endobj\n");
        text.Append("2 0 obj<</Type/Pages/Count 3/Kids[3 0 R 4 0 R 5 0 R]>>endobj\n");
        for (var index = 0; index < sizes.Length; index++)
        {
            text.Append($"{3 + index} 0 obj<</Type/Page/Parent 2 0 R/MediaBox[0 0 {sizes[index].Item1} {sizes[index].Item2}]>>endobj\n");
        }

        text.Append("trailer<</Root 1 0 R/Size 6>>\n%%EOF\n"); // no xref table: PDFium rebuilds it by scanning the objects
        return System.Text.Encoding.ASCII.GetBytes(text.ToString());
    }

    [Fact]
    public void Apply_WithQuarterTurns_RotatesOnlyThosePages()
    {
        using var document = PdfDocument.OpenFromBytes(ThreeShapes());
        Assert.Equal((100.0, 200.0), SizeOf(document, 0));

        PdfPageOrganizer.Apply(document, new[] { 0, 1, 2 }, new[] { 1, 0, 3 });

        Assert.Equal((200.0, 100.0), SizeOf(document, 0)); // a quarter turn swaps width and height
        Assert.Equal((300.0, 100.0), SizeOf(document, 1));
        Assert.Equal((400.0, 150.0), SizeOf(document, 2)); // so do three
    }

    [Fact]
    public void Apply_WithQuarterTurns_TheRotationFollowsThePageWhenItMoves()
    {
        using var document = PdfDocument.OpenFromBytes(ThreeShapes());

        // The turns are listed in the new order: the page that ends up first (the old third one) is the rotated one.
        PdfPageOrganizer.Apply(document, new[] { 2, 0 }, new[] { 1, 0 });

        Assert.Equal(2, document.PageCount);
        Assert.Equal((400.0, 150.0), SizeOf(document, 0));
        Assert.Equal((100.0, 200.0), SizeOf(document, 1));
    }

    [Fact]
    public void Apply_WithQuarterTurns_TakesThemModuloFour()
    {
        using var document = PdfDocument.OpenFromBytes(ThreeShapes());

        PdfPageOrganizer.Apply(document, new[] { 0, 1, 2 }, new[] { 4, 5, -4 });

        Assert.Equal((100.0, 200.0), SizeOf(document, 0)); // 4 = nothing
        Assert.Equal((100.0, 300.0), SizeOf(document, 1)); // 5 = one
        Assert.Equal((150.0, 400.0), SizeOf(document, 2)); // -4 = nothing
    }

    [Fact]
    public void Apply_WithQuarterTurns_SurvivesSavingAndAddsToTheRotationAlreadyThere()
    {
        var once = PdfPageOrganizer.Apply(ThreeShapes(), new[] { 0, 1, 2 }, quarterTurns: new[] { 0, 1, 0 });
        using (var reopened = PdfDocument.OpenFromBytes(once))
        {
            Assert.Equal((100.0, 300.0), SizeOf(reopened, 1));
        }

        // The rotation is added to the one the page already has: 90 + 90 = 180, upright shape again.
        var twice = PdfPageOrganizer.Apply(once, new[] { 0, 1, 2 }, quarterTurns: new[] { 0, 1, 0 });
        using var again = PdfDocument.OpenFromBytes(twice);
        Assert.Equal((300.0, 100.0), SizeOf(again, 1));

        // ...and a further quarter turn counter-clockwise (3) from 180 gives 90 again.
        var thrice = PdfPageOrganizer.Apply(twice, new[] { 0, 1, 2 }, quarterTurns: new[] { 0, 3, 0 });
        using var last = PdfDocument.OpenFromBytes(thrice);
        Assert.Equal((100.0, 300.0), SizeOf(last, 1));
    }

    [Fact]
    public void Apply_WithQuarterTurns_KeepsTheOutline()
    {
        var original = File.ReadAllBytes(FixturePath("bookmarks.pdf"));

        var rotated = PdfPageOrganizer.Apply(original, new[] { 2, 0, 1, 3 }, quarterTurns: new[] { 1, 0, 0, 2 });

        using var reopened = PdfDocument.OpenFromBytes(rotated);
        Assert.Equal(new[] { "Capitolo 1", "Capitolo 2", "Sito web" }, reopened.GetBookmarks().Select(b => b.Title));
        Assert.Equal(1, PageOf(reopened, "Capitolo 1"));
    }

    [Fact]
    public void Apply_WithTheWrongNumberOfQuarterTurns_Throws()
    {
        using var document = OpenBookmarks();

        Assert.Throws<ArgumentException>(() => PdfPageOrganizer.Apply(document, new[] { 0, 1 }, new[] { 1 }));
        Assert.Equal(4, document.PageCount);
    }

    [Fact]
    public void Apply_WithAPageListedTwice_ThrowsAndChangesNothing()
    {
        using var document = OpenBookmarks();

        Assert.Throws<ArgumentException>(() => PdfPageOrganizer.Apply(document, new[] { 0, 1, 1 }));
        Assert.Equal(4, document.PageCount);
    }
}
