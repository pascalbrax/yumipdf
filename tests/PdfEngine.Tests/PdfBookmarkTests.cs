using PdfEngine;
using Xunit;

namespace PdfEngine.Tests;

public class PdfBookmarkTests
{
    private static string FixturePath(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    [Fact]
    public void GetBookmarks_ReadsTheOutlineTreeInOrder()
    {
        using var document = PdfDocument.OpenFromPath(FixturePath("bookmarks.pdf"));

        var bookmarks = document.GetBookmarks();

        Assert.Equal(new[] { "Capitolo 1", "Capitolo 2", "Sito web" }, bookmarks.Select(b => b.Title));
        Assert.Equal(new[] { "Sezione 1.1", "Sezione 1.2" }, bookmarks[0].Children.Select(b => b.Title));
        Assert.Equal(new[] { "Città e più" }, bookmarks[1].Children.Select(b => b.Title));
        Assert.Empty(bookmarks[2].Children);
    }

    [Fact]
    public void GetBookmarks_ResolvesDirectDestinationsAndGoToActions()
    {
        using var document = PdfDocument.OpenFromPath(FixturePath("bookmarks.pdf"));

        var bookmarks = document.GetBookmarks();

        Assert.Equal(0, bookmarks[0].PageIndex);             // direct /Dest
        Assert.Equal(1, bookmarks[0].Children[0].PageIndex); // direct /Dest
        Assert.Equal(2, bookmarks[0].Children[1].PageIndex); // /A << /S /GoTo >>
        Assert.Equal(3, bookmarks[1].PageIndex);
    }

    [Fact]
    public void GetBookmarks_EntryWithoutDestinationInTheDocument_HasNoPage()
    {
        using var document = PdfDocument.OpenFromPath(FixturePath("bookmarks.pdf"));

        var webLink = document.GetBookmarks()[2];

        Assert.Equal("Sito web", webLink.Title);
        Assert.Equal(-1, webLink.PageIndex);
    }

    [Fact]
    public void GetBookmarks_ReportsWhichEntriesStartOpen()
    {
        using var document = PdfDocument.OpenFromPath(FixturePath("bookmarks.pdf"));

        var bookmarks = document.GetBookmarks();

        Assert.True(bookmarks[0].IsInitiallyOpen);   // /Count 2
        Assert.False(bookmarks[1].IsInitiallyOpen);  // /Count -1
        Assert.False(bookmarks[2].IsInitiallyOpen);  // no children
    }

    [Fact]
    public void GetBookmarks_DocumentWithoutOutline_ReturnsEmpty()
    {
        using var document = PdfDocument.OpenFromPath(FixturePath("text-page.pdf"));

        Assert.Empty(document.GetBookmarks());
    }
}
