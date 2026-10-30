using PdfEngine.Native;

namespace PdfEngine.Organize;

/// <summary>
/// Reorders and removes the pages of an open document <em>in place</em> (<c>FPDF_MovePages</c> and <c>FPDFPage_Delete</c>), unlike
/// <see cref="PdfMergeSplitService.Reorder"/>, which builds a new document from the imported pages and so loses the outline
/// (bookmarks), the interactive form and the document information. Here all of that stays with the document: a bookmark follows
/// the page it points to, and one that pointed to a removed page is left without a destination.
/// </summary>
public static class PdfPageOrganizer
{
    /// <summary>
    /// Rearranges <paramref name="document"/> so its pages are, in this order, the ones listed in <paramref name="newOrder"/> -
    /// 0-based indices of the pages <em>as they are now</em>. A page that is not listed is removed.
    /// </summary>
    /// <exception cref="ArgumentException">Nothing is listed (a document needs a page), an index is out of range, or one is listed twice.</exception>
    public static void Apply(PdfDocument document, IReadOnlyList<int> newOrder)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(newOrder);

        var pageCount = document.PageCount;
        if (newOrder.Count == 0)
        {
            throw new ArgumentException("Un documento deve avere almeno una pagina.", nameof(newOrder));
        }

        var kept = new HashSet<int>();
        foreach (var index in newOrder)
        {
            if (index < 0 || index >= pageCount)
            {
                throw new ArgumentException($"La pagina {index + 1} non esiste (il documento ne ha {pageCount}).", nameof(newOrder));
            }

            if (!kept.Add(index))
            {
                throw new ArgumentException($"La pagina {index + 1} compare più di una volta.", nameof(newOrder));
            }
        }

        PdfiumRuntime.Invoke(() =>
        {
            // Highest index first, so the indices of the pages still to be removed do not shift under us.
            for (var index = pageCount - 1; index >= 0; index--)
            {
                if (!kept.Contains(index))
                {
                    PdfiumNative.FPDFPage_Delete(document.Handle, index);
                }
            }

            // The survivors are now in their original relative order: a page's new position is its rank among them.
            var rank = new Dictionary<int, int>(newOrder.Count);
            var position = 0;
            for (var index = 0; index < pageCount; index++)
            {
                if (kept.Contains(index))
                {
                    rank[index] = position++;
                }
            }

            var moves = newOrder.Select(index => rank[index]).ToArray();
            if (moves.Select((page, target) => page == target).All(same => same))
            {
                return;
            }

            // Moving every page, in the wanted order, to the front is the whole reordering in one call.
            if (PdfiumNative.FPDF_MovePages(document.Handle, moves, (uint)moves.Length, 0) == 0)
            {
                throw PdfException.FromLastPdfiumError("Impossibile riordinare le pagine.");
            }
        });
    }

    /// <summary>
    /// Opens <paramref name="pdf"/> (a whole PDF file in memory), applies <see cref="Apply(PdfDocument, IReadOnlyList{int})"/> and
    /// returns the result as a new file's bytes. The input is not modified.
    /// </summary>
    public static byte[] Apply(byte[] pdf, IReadOnlyList<int> newOrder, string? password = null)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        using var document = PdfDocument.OpenFromBytes(pdf, password);
        Apply(document, newOrder);
        return document.SaveToBytes();
    }
}
