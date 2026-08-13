using PdfEngine.Native;

namespace PdfEngine.Organize;

/// <summary>Merge, split and reorder operations built on PDFium's page-organizer API (fpdf_ppo.h).</summary>
public static class PdfMergeSplitService
{
    /// <summary>Concatenates PDFs, in the given order, into a single output file.</summary>
    public static void Merge(IReadOnlyList<string> sourcePaths, string destinationPath)
    {
        ArgumentNullException.ThrowIfNull(sourcePaths);
        if (sourcePaths.Count == 0)
        {
            throw new ArgumentException("Specificare almeno un file da unire.", nameof(sourcePaths));
        }

        using var output = PdfDocument.CreateEmpty();

        foreach (var sourcePath in sourcePaths)
        {
            using var source = PdfDocument.OpenFromPath(sourcePath);
            ImportAllPages(source, output);
        }

        output.SaveAs(destinationPath);
    }

    /// <summary>
    /// Splits <paramref name="sourcePath"/> into one output file per entry of
    /// <paramref name="pageRanges"/>. Ranges use 0-based, inclusive page indices.
    /// </summary>
    public static void Split(
        string sourcePath, IReadOnlyList<(int FirstPage, int LastPage)> pageRanges, string destinationDirectory)
    {
        ArgumentNullException.ThrowIfNull(pageRanges);
        Directory.CreateDirectory(destinationDirectory);

        using var source = PdfDocument.OpenFromPath(sourcePath);
        var baseName = Path.GetFileNameWithoutExtension(sourcePath);

        for (var i = 0; i < pageRanges.Count; i++)
        {
            var (first, last) = pageRanges[i];
            if (first < 0 || last < first || last >= source.PageCount)
            {
                throw new ArgumentOutOfRangeException(nameof(pageRanges), $"Intervallo pagine non valido: {first}-{last}.");
            }

            using var output = PdfDocument.CreateEmpty();
            var indices = Enumerable.Range(first, last - first + 1).ToArray();
            ImportPages(source, output, indices);

            var outputPath = Path.Combine(destinationDirectory, $"{baseName}_{i + 1:D2}.pdf");
            output.SaveAs(outputPath);
        }
    }

    /// <summary>Writes a copy of <paramref name="sourcePath"/> with pages reordered/subset per <paramref name="newPageOrder"/> (0-based source indices).</summary>
    public static void Reorder(string sourcePath, IReadOnlyList<int> newPageOrder, string destinationPath)
    {
        using var source = PdfDocument.OpenFromPath(sourcePath);
        using var output = PdfDocument.CreateEmpty();
        ImportPages(source, output, newPageOrder.ToArray());
        output.SaveAs(destinationPath);
    }

    private static void ImportAllPages(PdfDocument source, PdfDocument destination)
    {
        var indices = Enumerable.Range(0, source.PageCount).ToArray();
        ImportPages(source, destination, indices);
    }

    private static void ImportPages(PdfDocument source, PdfDocument destination, int[] pageIndices)
    {
        // Append at the current end of the destination document (0 == first page, so the
        // current page count is always "just past the last page").
        var insertIndex = destination.PageCount;
        var ok = PdfiumNative.FPDF_ImportPagesByIndex(
            destination.Handle, source.Handle, pageIndices, (uint)pageIndices.Length, insertIndex);
        if (ok == 0)
        {
            throw PdfException.FromLastPdfiumError("Impossibile importare le pagine nel documento di destinazione.");
        }
    }
}
