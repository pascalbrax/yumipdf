namespace PdfEngine.Viewing;

/// <summary>
/// The pages of a document that keep a rendered bitmap in memory: the page being read, the
/// <see cref="PagesBefore"/> before it and the <see cref="PagesAfter"/> after it (the ones that exist), plus
/// whatever else is on screen - at a small zoom the viewport shows far more than seven pages, and those must not
/// be blank. Every other page is only a correctly-sized placeholder. Only the maths lives here; rendering
/// and releasing bitmaps is the app's.
/// </summary>
public sealed class PageWindow
{
    /// <summary>How many pages before the current one stay rendered.</summary>
    public const int PagesBefore = 3;

    /// <summary>How many pages after the current one stay rendered.</summary>
    public const int PagesAfter = 3;

    private readonly int _currentFirst;
    private readonly int _currentLast;
    private readonly int _viewFirst;
    private readonly int _viewLast;

    private PageWindow(int current, int currentFirst, int currentLast, int viewFirst, int viewLast)
    {
        Current = current;
        _currentFirst = currentFirst;
        _currentLast = currentLast;
        _viewFirst = viewFirst;
        _viewLast = viewLast;
    }

    /// <summary>The page the window is centered on (clamped to the document).</summary>
    public int Current { get; }

    /// <summary>
    /// The window for a document of <paramref name="pageCount"/> pages. <paramref name="viewFirst"/>..<paramref name="viewLast"/>
    /// are the pages the viewport shows (-1 when not known yet). The two ranges are kept apart rather than merged:
    /// while the view jumps to a far page (thumbnails, search) the current page is already the target and the viewport
    /// still shows the old place, and merging would keep everything in between.
    /// </summary>
    public static PageWindow Compute(int pageCount, int current, int viewFirst, int viewLast)
    {
        if (pageCount <= 0)
        {
            return new PageWindow(0, 0, -1, 0, -1);
        }

        current = Math.Clamp(current, 0, pageCount - 1);
        var first = Math.Max(0, current - PagesBefore);
        var last = Math.Min(pageCount - 1, current + PagesAfter);

        if (viewFirst < 0 || viewLast < viewFirst)
        {
            return new PageWindow(current, first, last, 0, -1);
        }

        return new PageWindow(current, first, last, Math.Max(0, viewFirst), Math.Min(pageCount - 1, viewLast));
    }

    public bool Contains(int pageIndex)
        => (pageIndex >= _currentFirst && pageIndex <= _currentLast)
            || (pageIndex >= _viewFirst && pageIndex <= _viewLast);

    /// <summary>The pages of the window, nearest to the current one first (the later page first when two are equally far - people read forward).</summary>
    public IReadOnlyList<int> InRenderOrder()
    {
        var pages = new List<int>();
        for (var index = _currentFirst; index <= _currentLast; index++)
        {
            pages.Add(index);
        }

        for (var index = _viewFirst; index <= _viewLast; index++)
        {
            if (index < _currentFirst || index > _currentLast)
            {
                pages.Add(index);
            }
        }

        pages.Sort((a, b) =>
        {
            var byDistance = Math.Abs(a - Current).CompareTo(Math.Abs(b - Current));
            return byDistance != 0 ? byDistance : b.CompareTo(a);
        });
        return pages;
    }

    /// <summary>
    /// The first and last page that intersect the viewport, given pages laid out top to bottom with
    /// <paramref name="padding"/> above the first and <paramref name="spacing"/> between pages (-1, -1 for no pages).
    /// <paramref name="offset"/> is the scroll offset, <paramref name="viewportHeight"/> the visible height, both in the pixels of <paramref name="pageHeight"/>.
    /// </summary>
    public static (int First, int Last) VisiblePages(int pageCount, Func<int, double> pageHeight, double padding, double spacing, double offset, double viewportHeight)
    {
        if (pageCount <= 0)
        {
            return (-1, -1);
        }

        var first = -1;
        var last = -1;
        var top = padding;
        var viewBottom = offset + Math.Max(0, viewportHeight);
        for (var index = 0; index < pageCount; index++)
        {
            var bottom = top + pageHeight(index);
            if (bottom >= offset && top <= viewBottom)
            {
                if (first < 0)
                {
                    first = index;
                }

                last = index;
            }
            else if (top > viewBottom)
            {
                break;
            }

            top = bottom + spacing;
        }

        // Scrolled past the end (or an empty viewport): the last page is the nearest thing to show.
        return first < 0 ? (pageCount - 1, pageCount - 1) : (first, last);
    }
}
