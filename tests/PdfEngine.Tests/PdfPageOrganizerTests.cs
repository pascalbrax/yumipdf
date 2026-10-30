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

    [Fact]
    public void Apply_WithAPageListedTwice_ThrowsAndChangesNothing()
    {
        using var document = OpenBookmarks();

        Assert.Throws<ArgumentException>(() => PdfPageOrganizer.Apply(document, new[] { 0, 1, 1 }));
        Assert.Equal(4, document.PageCount);
    }
}
