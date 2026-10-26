namespace PdfEngine.Viewing;

/// <summary>
/// How a document whose pages differ in size (a landscape A3 among forty portrait A4) is shown. Whatever the mode, every
/// page is centered in a row at least as wide as the window, so a narrow page is never pushed off to one side by a wide
/// one elsewhere in the document; the modes differ in what "zoom" and "Adatta a larghezza" mean.
/// </summary>
public enum MixedPagesMode
{
    /// <summary>Every page at the same scale; "Adatta a larghezza" fits the page being read (the zoom follows the page chosen when it is clicked).</summary>
    CurrentPage,

    /// <summary>Every page at the same scale; "Adatta a larghezza" fits the width most pages have, so the odd wide page overflows and scrolls sideways.</summary>
    CommonWidth,

    /// <summary>Every page scaled to the same width (the most common one), whatever its own size: no sideways scrolling, but a wide page is shown smaller than its neighbours.</summary>
    SameWidth,
}

/// <summary>The maths behind <see cref="MixedPagesMode"/>; showing pages is the app's.</summary>
public static class PageLayout
{
    /// <summary>
    /// The page width, in points, that most pages have (widths compared to the nearest point); the first one reached when
    /// several are equally common. 0 for no pages.
    /// </summary>
    public static double CommonWidth(IReadOnlyList<double> widths)
    {
        var counts = new Dictionary<double, int>();
        var order = new List<double>();
        foreach (var width in widths)
        {
            var key = Math.Round(width);
            if (counts.TryGetValue(key, out var count))
            {
                counts[key] = count + 1;
            }
            else
            {
                counts[key] = 1;
                order.Add(key);
            }
        }

        var best = 0.0;
        var bestCount = 0;
        foreach (var key in order)
        {
            if (counts[key] > bestCount)
            {
                best = key;
                bestCount = counts[key];
            }
        }

        return best;
    }

    /// <summary>The width, in points, that "Adatta a larghezza" fits to the window.</summary>
    public static double FitReferenceWidth(MixedPagesMode mode, double currentPageWidth, double commonWidth)
        => mode == MixedPagesMode.CurrentPage || commonWidth <= 0 ? currentPageWidth : commonWidth;

    /// <summary>The factor a page is scaled by, on top of the zoom: 1, except in <see cref="MixedPagesMode.SameWidth"/> where it brings the page to the common width.</summary>
    public static double LayoutFactor(MixedPagesMode mode, double commonWidth, double pageWidth)
        => mode == MixedPagesMode.SameWidth && commonWidth > 0 && pageWidth > 0 ? commonWidth / pageWidth : 1;

    /// <summary>The longest side, in points, any page has once scaled for <paramref name="mode"/> - what the largest zoom has to keep under the render limit.</summary>
    public static double LongestSide(IReadOnlyList<(double Width, double Height)> sizes, MixedPagesMode mode, double commonWidth)
    {
        var longest = 1.0;
        foreach (var (width, height) in sizes)
        {
            longest = Math.Max(longest, Math.Max(width, height) * LayoutFactor(mode, commonWidth, width));
        }

        return longest;
    }
}
