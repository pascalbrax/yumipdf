using PdfEngine.Viewing;
using Xunit;

namespace PdfEngine.Tests;

public class PageWindowTests
{
    private static IEnumerable<int> Pages(PageWindow window, int pageCount)
        => Enumerable.Range(0, pageCount).Where(window.Contains);

    [Fact]
    public void Keeps_the_current_page_and_three_on_each_side()
    {
        var window = PageWindow.Compute(100, current: 50, viewFirst: -1, viewLast: -1);

        Assert.Equal(new[] { 47, 48, 49, 50, 51, 52, 53 }, Pages(window, 100));
    }

    [Fact]
    public void Stops_at_the_first_and_last_page_of_the_document()
    {
        Assert.Equal(new[] { 0, 1, 2, 3 }, Pages(PageWindow.Compute(100, 0, -1, -1), 100));
        Assert.Equal(new[] { 97, 98, 99 }.Prepend(96), Pages(PageWindow.Compute(100, 99, -1, -1), 100));
        Assert.Equal(new[] { 0, 1 }, Pages(PageWindow.Compute(2, 1, -1, -1), 2));
    }

    [Fact]
    public void A_viewport_showing_more_pages_than_the_window_keeps_them_all()
    {
        // Zoomed far out: pages 10..40 are on screen, the user is reading page 25.
        var window = PageWindow.Compute(100, 25, 10, 40);

        Assert.Equal(Enumerable.Range(10, 31), Pages(window, 100));
    }

    [Fact]
    public void A_jump_does_not_keep_the_pages_in_between()
    {
        // The target (80) is already the current page; the viewport still shows the old place (2..4).
        var window = PageWindow.Compute(100, 80, 2, 4);

        Assert.Equal(new[] { 2, 3, 4, 77, 78, 79, 80, 81, 82, 83 }, Pages(window, 100));
    }

    [Fact]
    public void Render_order_is_nearest_to_the_current_page_first_and_later_pages_first_on_a_tie()
    {
        var window = PageWindow.Compute(100, 10, -1, -1);

        Assert.Equal(new[] { 10, 11, 9, 12, 8, 13, 7 }, window.InRenderOrder());
    }

    [Fact]
    public void Render_order_has_every_page_once_even_when_the_ranges_overlap()
    {
        var window = PageWindow.Compute(100, 10, 8, 14);
        var order = window.InRenderOrder();

        Assert.Equal(order.Count, order.Distinct().Count());
        Assert.Equal(Pages(window, 100).OrderBy(i => i), order.OrderBy(i => i));
    }

    [Fact]
    public void An_empty_document_has_an_empty_window()
    {
        var window = PageWindow.Compute(0, 0, -1, -1);

        Assert.Empty(window.InRenderOrder());
        Assert.False(window.Contains(0));
    }

    [Fact]
    public void A_current_page_beyond_the_end_is_clamped()
    {
        Assert.Equal(new[] { 6, 7, 8, 9 }, Pages(PageWindow.Compute(10, 99, -1, -1), 10));
    }

    // Pages 100 px tall, 10 px of padding above the first and 20 px between them: page n occupies [10 + 120n, 110 + 120n].
    private static (int, int) Visible(double offset, double viewport, int pageCount = 100)
        => PageWindow.VisiblePages(pageCount, _ => 100, 10, 20, offset, viewport);

    [Theory]
    [InlineData(0, 100, 0, 0)]
    [InlineData(0, 300, 0, 2)]
    [InlineData(115, 50, 1, 1)]      // 115..165 starts in the gap after page 0 (ends at 110) and reaches page 1 (130..230)
    [InlineData(100, 50, 0, 1)]      // 100..150 sees the end of page 0 (until 110) and the start of page 1 (from 130)
    [InlineData(250, 10, 2, 2)]      // 250..260 is inside page 2 (250..350)
    [InlineData(12100, 600, 99, 99)] // scrolled past the end: the last page is the nearest thing to show
    public void Visible_pages_are_the_ones_that_intersect_the_viewport(double offset, double viewport, int first, int last)
        => Assert.Equal((first, last), Visible(offset, viewport));

    [Fact]
    public void Visible_pages_of_an_empty_document_are_none()
        => Assert.Equal((-1, -1), Visible(0, 500, pageCount: 0));
}
