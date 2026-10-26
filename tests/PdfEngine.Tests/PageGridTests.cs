using PdfEngine.Annotations;
using Xunit;

namespace PdfEngine.Tests;

public class PageGridTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(4.9, 0)]
    [InlineData(5, 10)]
    [InlineData(14, 10)]
    [InlineData(96.4, 100)]
    public void SnapX_goes_to_the_nearest_multiple_of_the_step(double x, double expected)
        => Assert.Equal(expected, PageGrid.SnapX(x));

    [Fact]
    public void Rows_are_counted_from_the_top_of_the_page_not_from_the_bottom()
    {
        // A4 is 842 pt tall: the row 10 pt below the top edge is y = 832, not y = 840 (the nearest multiple of 10 from the bottom).
        Assert.Equal(832, PageGrid.SnapTop(831, 842), 9);
        Assert.Equal(842, PageGrid.SnapTop(840, 842), 9);
        Assert.Equal(742, PageGrid.SnapTop(745, 842), 9);
    }

    [Fact]
    public void SnapPosition_moves_the_top_left_corner_onto_the_grid_and_keeps_the_size()
    {
        var snapped = PageGrid.SnapPosition(new PdfRect(103, 497, 283, 519), 842);
        Assert.Equal(100, snapped.Left, 9);
        Assert.Equal(180, snapped.Width, 9);
        Assert.Equal(22, snapped.Height, 9);
        Assert.Equal(0, (842 - snapped.Top) % PageGrid.StepPt, 9);
    }

    [Fact]
    public void Three_fields_stacked_by_hand_end_up_with_the_same_left_edge_and_evenly_spaced_tops()
    {
        double[] lefts = { 101.2, 98.7, 103.9 };
        double[] tops = { 702.4, 672.1, 641.9 }; // stacked by hand, about 30 pt apart but not exactly
        var rects = lefts.Zip(tops, (l, t) => PageGrid.SnapPosition(new PdfRect(l, t - 22, l + 180, t), 842)).ToArray();
        Assert.All(rects, r => Assert.Equal(100, r.Left, 9));
        Assert.Equal(rects[0].Top - rects[1].Top, rects[1].Top - rects[2].Top, 9);
        Assert.Equal(0, (rects[0].Top - rects[1].Top) % PageGrid.StepPt, 9);
    }
}
