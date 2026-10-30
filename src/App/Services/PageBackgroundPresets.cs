using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Pdf26.Services;

/// <summary>One of the ready-made colors for the area around the pages ("Opzioni").</summary>
public sealed record PageBackgroundPreset(string Name, string Hex)
{
    public Color Color => ColorHex.Parse(Hex);

    /// <summary>A fresh brush each time: brushes are UI objects and must not be shared across the dialog's items and the page area.</summary>
    public Brush Brush => new SolidColorBrush(Color);
}

/// <summary>
/// The 16 pastel colors offered for the page-area background, in the order they are shown (four rows of four: warm to cool, then
/// neutrals). Light enough that a white page keeps standing out from the background.
/// </summary>
public static class PageBackgroundPresets
{
    public static IReadOnlyList<PageBackgroundPreset> All { get; } = new[]
    {
        new PageBackgroundPreset("Rosa", "#FFF8C8DC"),
        new PageBackgroundPreset("Corallo", "#FFF9B7B0"),
        new PageBackgroundPreset("Pesca", "#FFFFD8B8"),
        new PageBackgroundPreset("Burro", "#FFFFF1A8"),
        new PageBackgroundPreset("Limone", "#FFEAF3A8"),
        new PageBackgroundPreset("Salvia", "#FFC5DDB8"),
        new PageBackgroundPreset("Menta", "#FFC8F0D0"),
        new PageBackgroundPreset("Acquamarina", "#FFB8EBE0"),
        new PageBackgroundPreset("Cielo", "#FFBFE3F7"),
        new PageBackgroundPreset("Pervinca", "#FFC8CCF5"),
        new PageBackgroundPreset("Lavanda", "#FFD1C8FF"),
        new PageBackgroundPreset("Lilla", "#FFE2C8F5"),
        new PageBackgroundPreset("Orchidea", "#FFF1C8EE"),
        new PageBackgroundPreset("Perla", "#FFE4E4E8"),
        new PageBackgroundPreset("Sabbia", "#FFEADFCB"),
        new PageBackgroundPreset("Tortora", "#FFD9CFC7"),
    };

    /// <summary>The preset with exactly this color (alpha ignored), or null - for a color chosen before the presets existed.</summary>
    public static PageBackgroundPreset? Find(Color color)
        => All.FirstOrDefault(preset => preset.Color.R == color.R && preset.Color.G == color.G && preset.Color.B == color.B);
}
