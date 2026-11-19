namespace PdfEngine.Viewing;

/// <summary>
/// The name of a colour, in Italian - for the colour detector ("Opzioni" > daltonismo): the palette entry nearest to an RGB value.
/// "Nearest" is measured in CIE L*a*b*, where a distance is about what the eye sees as different (RGB distances are not: two greens
/// far apart in RGB can look alike). The palette is a plain list of about seventy everyday names, with light/dark variants
/// of the main hues, so the answer is the word a person would use ("Verde scuro", "Rosa", "Marrone"), not a catalogue name.
/// </summary>
public static class ColorNamer
{
    /// <summary>A distance (CIE76 delta E) below which a colour is not told apart from the palette entry: the name is then given without "più vicino".</summary>
    public const double ExactThreshold = 3.0;

    private static readonly (string Name, byte R, byte G, byte B)[] Palette =
    {
        // Neutrals
        ("Nero", 0, 0, 0),
        ("Grigio molto scuro", 51, 51, 51),
        ("Grigio scuro", 96, 96, 96),
        ("Grigio", 128, 128, 128),
        ("Grigio chiaro", 192, 192, 192),
        ("Grigio molto chiaro", 227, 227, 227),
        ("Bianco", 255, 255, 255),
        ("Avorio", 255, 255, 240),
        ("Crema", 255, 253, 208),
        ("Beige", 245, 235, 208),

        // Reds and pinks
        ("Rosso", 255, 0, 0),
        ("Rosso scuro", 139, 0, 0),
        ("Rosso chiaro", 255, 102, 102),
        ("Rosso mattone", 178, 34, 34),
        ("Rosa antico", 190, 105, 110),
        ("Cremisi", 220, 20, 60),
        ("Bordeaux", 128, 0, 32),
        ("Rosa", 255, 192, 203),
        ("Rosa chiaro", 255, 224, 230),
        ("Rosa acceso", 255, 105, 180),
        ("Salmone", 250, 128, 114),
        ("Corallo", 255, 127, 80),
        ("Fucsia", 255, 20, 147),
        ("Magenta", 255, 0, 255),

        // Oranges, yellows
        ("Arancione", 255, 128, 0),
        ("Arancione scuro", 204, 85, 0),
        ("Arancione chiaro", 255, 179, 102),
        ("Pesca", 255, 218, 185),
        ("Giallo", 255, 255, 0),
        ("Giallo chiaro", 255, 255, 153),
        ("Oro", 255, 215, 0),
        ("Senape", 225, 173, 1),
        ("Ocra", 204, 119, 34),
        ("Cachi", 240, 230, 140),

        // Browns
        ("Marrone", 139, 69, 19),
        ("Marrone scuro", 92, 51, 23),
        ("Marrone chiaro", 160, 120, 90),
        ("Cioccolato", 210, 105, 30),
        ("Terracotta", 226, 114, 91),
        ("Sabbia", 230, 199, 156),
        ("Nocciola", 193, 154, 107),

        // Greens
        ("Verde", 0, 128, 0),
        ("Verde chiaro", 144, 238, 144),
        ("Verde scuro", 0, 80, 0),
        ("Verde brillante", 0, 230, 0),
        ("Verde lime", 191, 255, 0),
        ("Verde oliva", 128, 128, 0),
        ("Verde menta", 152, 255, 152),
        ("Verde foresta", 34, 139, 34),
        ("Verde mare", 46, 139, 87),
        ("Verde smeraldo", 80, 200, 120),
        ("Verde salvia", 156, 175, 136),

        // Cyans and blues
        ("Verde petrolio", 0, 128, 128),
        ("Turchese", 64, 224, 208),
        ("Acquamarina", 127, 255, 212),
        ("Ciano", 0, 255, 255),
        ("Celeste", 135, 206, 235),
        ("Azzurro", 0, 127, 255),
        ("Azzurro polvere", 176, 196, 222),
        ("Blu acciaio", 70, 130, 180),
        ("Blu reale", 65, 105, 225),
        ("Blu medio", 30, 120, 210),
        ("Blu", 0, 0, 255),
        ("Blu scuro", 0, 0, 139),
        ("Blu notte", 25, 25, 112),

        // Purples
        ("Viola", 128, 0, 128),
        ("Viola chiaro", 147, 112, 219),
        ("Indaco", 75, 0, 130),
        ("Lilla", 200, 162, 200),
        ("Lavanda", 230, 230, 250),
        ("Orchidea", 218, 112, 214),
        ("Prugna", 142, 69, 133),
        ("Melanzana", 97, 64, 81),
    };

    private static readonly (double L, double A, double B)[] PaletteLab = Palette.Select(p => ToLab(p.R, p.G, p.B)).ToArray();

    /// <summary>The number of names the palette has.</summary>
    public static int PaletteSize => Palette.Length;

    /// <summary>The nearest palette name to a colour, and how near it is (<see cref="ColorName.Distance"/>, 0 for the very same colour).</summary>
    public static ColorName Describe(byte r, byte g, byte b)
    {
        var lab = ToLab(r, g, b);
        var best = 0;
        var bestDistance = double.MaxValue;
        for (var i = 0; i < PaletteLab.Length; i++)
        {
            var dl = lab.L - PaletteLab[i].L;
            var da = lab.A - PaletteLab[i].A;
            var db = lab.B - PaletteLab[i].B;
            var distance = Math.Sqrt(dl * dl + da * da + db * db);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = i;
            }
        }

        return new ColorName(Palette[best].Name, bestDistance, bestDistance <= ExactThreshold);
    }

    /// <summary>The names of the palette, in the order they are listed (for tests and documentation).</summary>
    public static IReadOnlyList<(string Name, byte R, byte G, byte B)> Entries => Palette;

    /// <summary>sRGB (D65) to CIE L*a*b*.</summary>
    internal static (double L, double A, double B) ToLab(byte r, byte g, byte b)
    {
        static double Linear(byte c)
        {
            var v = c / 255.0;
            return v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        }

        var lr = Linear(r);
        var lg = Linear(g);
        var lb = Linear(b);
        var x = (0.4124564 * lr + 0.3575761 * lg + 0.1804375 * lb) / 0.95047;
        var y = 0.2126729 * lr + 0.7151522 * lg + 0.0721750 * lb;
        var z = (0.0193339 * lr + 0.1191920 * lg + 0.9503041 * lb) / 1.08883;

        static double F(double t) => t > 216.0 / 24389.0 ? Math.Cbrt(t) : (24389.0 / 27.0 * t + 16) / 116;

        var fx = F(x);
        var fy = F(y);
        var fz = F(z);
        return (116 * fy - 16, 500 * (fx - fy), 200 * (fy - fz));
    }
}

/// <summary>What <see cref="ColorNamer.Describe"/> found: the name, how far the colour is from it, and whether it is, for the eye, that very colour.</summary>
/// <param name="Name">Italian, e.g. "Verde scuro".</param>
/// <param name="Distance">CIE76 delta E to the palette entry: about 1 is barely visible, 10 clearly different.</param>
/// <param name="IsExact"><c>true</c> when the distance is within <see cref="ColorNamer.ExactThreshold"/>: the name is the colour's own, not only the nearest one.</param>
public readonly record struct ColorName(string Name, double Distance, bool IsExact);
