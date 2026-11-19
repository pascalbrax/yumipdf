using PdfEngine.Viewing;
using Xunit;

namespace PdfEngine.Tests;

public class ColorNamerTests
{
    [Fact]
    public void EveryPaletteColor_IsNamedItselfAndExact()
    {
        foreach (var (name, r, g, b) in ColorNamer.Entries)
        {
            var found = ColorNamer.Describe(r, g, b);
            Assert.Equal(name, found.Name);
            Assert.True(found.IsExact, name);
            Assert.Equal(0, found.Distance, 6);
        }
    }

    [Fact]
    public void PaletteNames_AreUnique_AndNoTwoEntriesLookTheSame()
    {
        var entries = ColorNamer.Entries;
        Assert.Equal(entries.Count, entries.Select(e => e.Name).Distinct().Count());

        // Two entries closer than the "exact" threshold could not be told apart by the detector.
        for (var i = 0; i < entries.Count; i++)
        {
            for (var j = i + 1; j < entries.Count; j++)
            {
                var a = ColorNamer.ToLab(entries[i].R, entries[i].G, entries[i].B);
                var b = ColorNamer.ToLab(entries[j].R, entries[j].G, entries[j].B);
                var d = Math.Sqrt(Math.Pow(a.L - b.L, 2) + Math.Pow(a.A - b.A, 2) + Math.Pow(a.B - b.B, 2));
                Assert.True(d > 2 * ColorNamer.ExactThreshold, $"{entries[i].Name} and {entries[j].Name} are {d:0.0} apart");
            }
        }
    }

    [Theory]
    [InlineData(250, 20, 20, "Rosso")]
    [InlineData(10, 10, 12, "Nero")]
    [InlineData(250, 250, 250, "Bianco")]
    [InlineData(120, 120, 124, "Grigio")]
    [InlineData(255, 140, 0, "Arancione")]
    [InlineData(20, 200, 40, "Verde brillante")]
    [InlineData(0, 90, 0, "Verde scuro")]
    [InlineData(135, 206, 250, "Celeste")]
    [InlineData(255, 250, 80, "Giallo")]
    [InlineData(200, 100, 100, "Rosa antico")]
    [InlineData(38, 131, 219, "Blu medio")]
    [InlineData(0, 120, 255, "Azzurro")]
    public void NearbyColors_GetTheirEverydayName(int r, int g, int b, string expected)
    {
        var found = ColorNamer.Describe((byte)r, (byte)g, (byte)b);
        Assert.Equal(expected, found.Name);
    }

    [Fact]
    public void AColorBetweenNames_IsNotExact_ButStillNamed()
    {
        var found = ColorNamer.Describe(120, 170, 90);
        Assert.False(found.IsExact);
        Assert.False(string.IsNullOrEmpty(found.Name));
        Assert.True(found.Distance > ColorNamer.ExactThreshold);
    }

    [Fact]
    public void LabConversion_HasTheKnownEndPoints()
    {
        var white = ColorNamer.ToLab(255, 255, 255);
        Assert.Equal(100, white.L, 1);
        Assert.Equal(0, white.A, 1);
        Assert.Equal(0, white.B, 1);

        var black = ColorNamer.ToLab(0, 0, 0);
        Assert.Equal(0, black.L, 3);

        var red = ColorNamer.ToLab(255, 0, 0);
        Assert.Equal(53.2, red.L, 0);
        Assert.Equal(80.1, red.A, 0);
    }
}
