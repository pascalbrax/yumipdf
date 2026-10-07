using System.Text;
using PdfEngine;
using Xunit;

namespace PdfEngine.Tests;

public class PdfAConformanceTests
{
    [Fact]
    public void Detect_WithAttributeForm_ReturnsConformance()
    {
        var xmp = """
            <rdf:Description rdf:about=""
                xmlns:pdfaid="http://www.aiim.org/pdfa/ns/id/"
                pdfaid:part="2"
                pdfaid:conformance="B"/>
            """;

        Assert.Equal("PDF/A-2B", PdfAConformance.Detect(Encoding.UTF8.GetBytes(xmp)));
    }

    [Fact]
    public void Detect_WithElementForm_ReturnsConformance()
    {
        var xmp = """
            <rdf:Description rdf:about="" xmlns:pdfaid="http://www.aiim.org/pdfa/ns/id/">
              <pdfaid:part>1</pdfaid:part>
              <pdfaid:conformance>a</pdfaid:conformance>
            </rdf:Description>
            """;

        Assert.Equal("PDF/A-1A", PdfAConformance.Detect(Encoding.UTF8.GetBytes(xmp)));
    }

    [Fact]
    public void Detect_WithoutPdfANamespace_ReturnsNull()
    {
        var bytes = Encoding.UTF8.GetBytes("<rdf:Description rdf:about=\"\"></rdf:Description>");

        Assert.Null(PdfAConformance.Detect(bytes));
    }

    [Fact]
    public void Detect_WithNamespaceButNoConformanceFields_ReturnsNull()
    {
        var bytes = Encoding.UTF8.GetBytes("xmlns:pdfaid=\"http://www.aiim.org/pdfa/ns/id/\"");

        Assert.Null(PdfAConformance.Detect(bytes));
    }

    [Fact]
    public void Detect_WithEmptyInput_ReturnsNull()
    {
        Assert.Null(PdfAConformance.Detect(Array.Empty<byte>()));
    }
}
