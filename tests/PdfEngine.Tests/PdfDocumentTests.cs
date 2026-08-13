using PdfEngine;
using PdfEngine.Organize;
using Xunit;

namespace PdfEngine.Tests;

public class PdfDocumentTests
{
    [Fact]
    public void CreateEmpty_HasZeroPages()
    {
        // Also doubles as a smoke test that PDFium initializes and loads cleanly.
        using var document = PdfDocument.CreateEmpty();
        Assert.Equal(0, document.PageCount);
    }

    [Fact]
    public void Merge_WithNoSources_Throws()
    {
        Assert.Throws<ArgumentException>(() => PdfMergeSplitService.Merge(Array.Empty<string>(), "out.pdf"));
    }
}
