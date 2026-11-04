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
    /// <param name="quarterTurns">
    /// Optional, same length as <paramref name="newOrder"/>: how many quarter turns <em>clockwise</em> to add to each listed page's
    /// own rotation (<c>/Rotate</c>), in the same order - <c>0</c> leaves a page as it is, <c>3</c> is a quarter turn counter-clockwise.
    /// Any whole number is accepted (it is taken modulo 4). The page keeps its content; only the way it is shown changes.
    /// </param>
    /// <exception cref="ArgumentException">Nothing is listed (a document needs a page), an index is out of range or listed twice, or <paramref name="quarterTurns"/> has the wrong length.</exception>
    public static void Apply(PdfDocument document, IReadOnlyList<int> newOrder, IReadOnlyList<int>? quarterTurns = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(newOrder);
        if (quarterTurns is not null && quarterTurns.Count != newOrder.Count)
        {
            throw new ArgumentException("Serve una rotazione per ogni pagina elencata.", nameof(quarterTurns));
        }

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
            if (!moves.Select((page, target) => page == target).All(same => same))
            {
                // Moving every page, in the wanted order, to the front is the whole reordering in one call.
                if (PdfiumNative.FPDF_MovePages(document.Handle, moves, (uint)moves.Length, 0) == 0)
                {
                    throw PdfException.FromLastPdfiumError("Impossibile riordinare le pagine.");
                }
            }

            if (quarterTurns is null)
            {
                return;
            }

            // Rotations last, by the pages' new positions.
            for (var target = 0; target < quarterTurns.Count; target++)
            {
                var turns = ((quarterTurns[target] % 4) + 4) % 4;
                if (turns == 0)
                {
                    continue;
                }

                var pageHandle = PdfiumNative.FPDF_LoadPage(document.Handle, target);
                if (pageHandle == IntPtr.Zero)
                {
                    throw PdfException.FromLastPdfiumError($"Impossibile ruotare la pagina {target + 1}.");
                }

                try
                {
                    PdfiumNative.FPDFPage_SetRotation(pageHandle, (PdfiumNative.FPDFPage_GetRotation(pageHandle) + turns) % 4);
                }
                finally
                {
                    PdfiumNative.FPDF_ClosePage(pageHandle);
                }
            }
        });
    }

    /// <summary>
    /// Opens <paramref name="pdf"/> (a whole PDF file in memory), applies <see cref="Apply(PdfDocument, IReadOnlyList{int}, IReadOnlyList{int}?)"/> and
    /// returns the result as a new file's bytes. The input is not modified.
    /// </summary>
    public static byte[] Apply(byte[] pdf, IReadOnlyList<int> newOrder, string? password = null, IReadOnlyList<int>? quarterTurns = null)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        using var document = PdfDocument.OpenFromBytes(pdf, password);
        Apply(document, newOrder, quarterTurns);
        return document.SaveToBytes();
    }
}
