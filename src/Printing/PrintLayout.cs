namespace Printing;

/// <summary>All distances are in DIPs (1/96 inch), independent of render resolution.</summary>
public readonly record struct PrintPlacement(double X, double Y, double Width, double Height)
{
    public static PrintPlacement Fit(double pageWidth, double pageHeight,
        double paperWidth, double paperHeight, double imageX, double imageY, double imageWidth, double imageHeight)
    {
        if (!double.IsFinite(pageWidth) || !double.IsFinite(pageHeight) || pageWidth <= 0 || pageHeight <= 0 ||
            !double.IsFinite(paperWidth) || !double.IsFinite(paperHeight) || paperWidth <= 0 || paperHeight <= 0 ||
            !double.IsFinite(imageX) || !double.IsFinite(imageY) ||
            !double.IsFinite(imageWidth) || !double.IsFinite(imageHeight))
            throw new ArgumentOutOfRangeException(nameof(pageWidth), "Dimensioni di stampa non valide.");

        var left = Math.Clamp(imageX, 0, paperWidth);
        var top = Math.Clamp(imageY, 0, paperHeight);
        var width = Math.Min(paperWidth, imageX + imageWidth) - left;
        var height = Math.Min(paperHeight, imageY + imageHeight) - top;
        if (width <= 0 || height <= 0)
            throw new InvalidOperationException("La stampante non ha un'area stampabile valida.");

        var scale = Math.Min(width / pageWidth, height / pageHeight);
        var fittedWidth = pageWidth * scale;
        var fittedHeight = pageHeight * scale;
        return new(left + (width - fittedWidth) / 2, top + (height - fittedHeight) / 2, fittedWidth, fittedHeight);
    }

    /// <summary>Bounds both texture dimensions and pixel count, including unusually large paper.</summary>
    public (int Width, int Height) RasterSize(double dpi, int maxDimension = 8192, long maxPixels = 20_000_000)
    {
        if (!double.IsFinite(dpi) || dpi <= 0 || maxDimension <= 0 || maxPixels <= 0 || Width <= 0 || Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(dpi));
        var scale = Math.Min(dpi / 96, Math.Min(maxDimension / Width, maxDimension / Height));
        scale = Math.Min(scale, Math.Sqrt(maxPixels / (Width * Height)));
        return (Math.Max(1, (int)Math.Floor(Width * scale)), Math.Max(1, (int)Math.Floor(Height * scale)));
    }
}

public static class PrintPageSelection
{
    /// <summary>One-based source page numbers, sorted, deduplicated and clipped to the document.</summary>
    public static int[] Resolve(int pageCount, IEnumerable<(int First, int Last)> ranges)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageCount);
        var requested = ranges.ToArray();
        if (requested.Length == 0) return Enumerable.Range(1, pageCount).ToArray();
        var pages = new SortedSet<int>();
        foreach (var (first, last) in requested)
            for (var page = Math.Max(1, first); page <= Math.Min(pageCount, last); page++)
                pages.Add(page);
        if (pages.Count == 0) throw new InvalidOperationException("L'intervallo non contiene pagine del documento.");
        return pages.ToArray();
    }
}
