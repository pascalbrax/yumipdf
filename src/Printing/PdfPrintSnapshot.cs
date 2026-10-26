using PdfEngine;
using PdfEngine.Forms;

namespace Printing;

/// <summary>
/// An independent in-memory document owned by one print job. Edits, opening another file,
/// or cancelling printing cannot flatten or otherwise change the user's live document.
/// </summary>
public sealed class PdfPrintSnapshot : IDisposable
{
    private readonly PdfDocument _document;
    private readonly PdfFormFiller _formFiller;
    public IReadOnlyList<(double Width, double Height)> PageSizes { get; }
    public int PageCount => PageSizes.Count;

    private PdfPrintSnapshot(PdfDocument document)
    {
        if (document.PageCount == 0) throw new InvalidOperationException("Il documento non contiene pagine da stampare.");
        _document = document;
        _formFiller = new PdfFormFiller(document);
        try
        {
            PageSizes = Enumerable.Range(0, document.PageCount).Select(index =>
            {
                using var page = document.GetPage(index);
                return page.SizeInPoints;
            }).ToArray();
        }
        catch
        {
            _formFiller.Dispose();
            throw;
        }
    }

    public static PdfPrintSnapshot Capture(PdfDocument source, IReadOnlyList<PdfFormChange>? pendingFields = null)
        => FromBytes(source.SaveToBytes(removeSecurity: true), pendingFields);

    public static PdfPrintSnapshot FromBytes(byte[] bytes, IReadOnlyList<PdfFormChange>? pendingFields = null)
    {
        if (pendingFields is { Count: > 0 }) bytes = PdfFormDesigner.Apply(bytes, pendingFields);
        var document = PdfDocument.OpenFromBytes(bytes);
        try { return new PdfPrintSnapshot(document); }
        catch { document.Dispose(); throw; }
    }

    public byte[] Render(int pageIndex, int pixelWidth, int pixelHeight)
        => _formFiller.RenderPageToBgra(pageIndex, pixelWidth, pixelHeight);

    public void Dispose()
    {
        _formFiller.Dispose();
        _document.Dispose();
    }
}
