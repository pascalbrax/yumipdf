using Windows.UI;

namespace Pdf26.Services;

/// <summary>
/// <see cref="Color"/> has no built-in parser in WinRT (its <c>ToString()</c> already
/// produces "#AARRGGBB", which is why <see cref="ToHex"/> mirrors that format).
/// </summary>
public static class ColorHex
{
    public static Color Parse(string hex)
    {
        var value = hex.TrimStart('#');
        if (value.Length == 6)
        {
            value = "FF" + value; // no alpha specified: assume opaque
        }

        var a = Convert.ToByte(value[..2], 16);
        var r = Convert.ToByte(value[2..4], 16);
        var g = Convert.ToByte(value[4..6], 16);
        var b = Convert.ToByte(value[6..8], 16);
        return Color.FromArgb(a, r, g, b);
    }

    public static string ToHex(Color color) => $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";
}
