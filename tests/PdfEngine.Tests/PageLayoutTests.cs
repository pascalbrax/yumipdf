using PdfEngine.Viewing;
using Xunit;

namespace PdfEngine.Tests;

public class PageLayoutTests
{
    // The shape of the real case: portrait A4 pages with a few landscape A3 ones among them.
    private static readonly double[] MixedWidths = { 595, 595, 595, 1191, 595, 1191, 595, 842, 595 };

    [Fact]
    public void The_common_width_is_the_one_most_pages_have()
        => Assert.Equal(595, PageLayout.CommonWidth(MixedWidths));

    [Fact]
    public void Widths_are_compared_to_the_nearest_point()
        => Assert.Equal(595, PageLayout.CommonWidth(new[] { 595.2, 594.8, 595.4, 842 }));

    [Fact]
    public void With_a_tie_the_width_met_first_wins()
        => Assert.Equal(842, PageLayout.CommonWidth(new double[] { 842, 595, 595, 842 }));

    [Fact]
    public void No_pages_have_no_common_width()
        => Assert.Equal(0, PageLayout.CommonWidth(Array.Empty<double>()));

    [Theory]
    [InlineData(MixedPagesMode.CurrentPage, 1191)]
    [InlineData(MixedPagesMode.CommonWidth, 595)]
    [InlineData(MixedPagesMode.SameWidth, 595)]
    public void Fit_width_uses_the_current_page_only_in_the_first_mode(MixedPagesMode mode, double expected)
        => Assert.Equal(expected, PageLayout.FitReferenceWidth(mode, currentPageWidth: 1191, commonWidth: 595));

    [Fact]
    public void Fit_width_falls_back_to_the_current_page_without_a_common_width()
        => Assert.Equal(1191, PageLayout.FitReferenceWidth(MixedPagesMode.CommonWidth, 1191, 0));

    [Theory]
    [InlineData(MixedPagesMode.CurrentPage, 1)]
    [InlineData(MixedPagesMode.CommonWidth, 1)]
    [InlineData(MixedPagesMode.SameWidth, 0.5)]
    public void Only_the_same_width_mode_scales_a_page(MixedPagesMode mode, double expected)
        => Assert.Equal(expected, PageLayout.LayoutFactor(mode, commonWidth: 595.5, pageWidth: 1191), 3);

    [Fact]
    public void The_longest_side_follows_the_scaling_of_the_mode()
    {
        var sizes = new (double, double)[] { (595, 842), (1191, 842), (300, 400) };

        Assert.Equal(1191, PageLayout.LongestSide(sizes, MixedPagesMode.CurrentPage, 595));

        // Same width: the A3 page is halved (595 x 421), but the small one is doubled (595 x 793).
        Assert.Equal(842, PageLayout.LongestSide(sizes, MixedPagesMode.SameWidth, 595));
    }
}
