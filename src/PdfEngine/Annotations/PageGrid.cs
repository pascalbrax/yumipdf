namespace PdfEngine.Annotations;

/// <summary>
/// The alignment grid of a page: points every <see cref="StepPt"/> PDF points, counted from the page's
/// <em>top-left</em> corner (where a person starts looking), so "the left edge is on the grid" and "the top edge is on the grid"
/// mean the same thing at any zoom and on any page size. Only the maths lives here; drawing the dots and snapping while
/// dragging are the app's.
/// </summary>
public static class PageGrid
{
    /// <summary>Distance between grid points: 10 PDF points, about 3.5 mm.</summary>
    public const double StepPt = 10;

    /// <summary>The nearest grid column for an x coordinate (PDF points, from the left edge).</summary>
    public static double SnapX(double x) => Math.Round(x / StepPt, MidpointRounding.AwayFromZero) * StepPt;

    /// <summary>The nearest grid row for a y coordinate in PDF space (origin bottom-left): rows are counted down from the top of the page.</summary>
    public static double SnapTop(double y, double pageHeight)
        => pageHeight - Math.Round((pageHeight - y) / StepPt, MidpointRounding.AwayFromZero) * StepPt;

    /// <summary>The same rectangle (same size) moved so its left and top edges lie on the grid.</summary>
    public static PdfRect SnapPosition(PdfRect rect, double pageHeight)
    {
        var left = SnapX(rect.Left);
        var top = SnapTop(rect.Top, pageHeight);
        return new PdfRect(left, top - rect.Height, left + rect.Width, top);
    }
}
