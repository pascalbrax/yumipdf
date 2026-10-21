namespace PdfEngine.Editing;

/// <summary>What a TrueType/OpenType font file says about its own look.</summary>
/// <param name="Family">Serif, sans or monospaced, when the file says; null when it does not.</param>
/// <param name="Bold">Weight class 600 or more, or the bold style bit; null when the file does not say.</param>
/// <param name="Italic">The italic/oblique style bit or a non-zero italic angle; null when the file does not say.</param>
internal readonly record struct FontLook(FallbackFamily? Family, bool? Bold, bool? Italic);

/// <summary>
/// Reads the style tables of a font file (<c>OS/2</c>, <c>head</c>, <c>post</c>) to choose a fallback font that looks like
/// it. Far more reliable than what a PDF says about the font: the descriptor's flags are whatever the producer felt
/// like (measured on a real document: an Arial-like bold font declared as serif and symbolic, under the name
/// "CIDFont+F2"), while the file's own panose and weight class describe the shapes themselves. A font subset keeps
/// these tables in practice; when it does not, the result is simply less certain.
/// </summary>
internal static class SfntInfo
{
    public static FontLook Read(byte[] data)
    {
        try
        {
            return ReadCore(data);
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException)
        {
            return default; // a damaged or unusual file: say nothing rather than guess
        }
    }

    private static FontLook ReadCore(byte[] d)
    {
        if (d.Length < 12)
        {
            return default;
        }

        var version = U32(d, 0);
        if (version is not (0x00010000 or 0x74727565 /* true */ or 0x4F54544F /* OTTO */))
        {
            return default;
        }

        var tableCount = U16(d, 4);
        int os2 = -1, os2Length = 0, head = -1, post = -1;
        for (var i = 0; i < tableCount; i++)
        {
            var record = 12 + i * 16;
            if (record + 16 > d.Length)
            {
                break;
            }

            var tag = U32(d, record);
            var offset = (int)U32(d, record + 8);
            var length = (int)U32(d, record + 12);
            switch (tag)
            {
                case 0x4F532F32: // OS/2
                    os2 = offset;
                    os2Length = length;
                    break;
                case 0x68656164: // head
                    head = offset;
                    break;
                case 0x706F7374: // post
                    post = offset;
                    break;
            }
        }

        FallbackFamily? family = null;
        bool? bold = null, italic = null;

        if (post >= 0 && post + 16 <= d.Length)
        {
            if (U32(d, post + 12) != 0)
            {
                family = FallbackFamily.Mono; // isFixedPitch
            }

            var angle = (int)U32(d, post + 4) / 65536.0; // Fixed 16.16, degrees counter-clockwise from vertical
            if (angle != 0)
            {
                italic = true;
            }
        }

        if (head >= 0 && head + 46 <= d.Length)
        {
            var macStyle = U16(d, head + 44);
            bold = (macStyle & 1) != 0 ? true : bold;
            italic = (macStyle & 2) != 0 ? true : italic;
        }

        if (os2 >= 0 && os2Length >= 64 && os2 + 64 <= d.Length)
        {
            var weight = U16(d, os2 + 4);
            if (weight is >= 100 and <= 900)
            {
                bold = weight >= 600;
            }

            var selection = U16(d, os2 + 62);
            if ((selection & 0x20) != 0)
            {
                bold = true;
            }

            if ((selection & 0x01) != 0)
            {
                italic = true;
            }

            if (family is null)
            {
                family = FamilyFromClassification(d, os2);
            }
        }

        return new FontLook(family, bold, italic);
    }

    /// <summary>Serif or sans from the panose digits, or from the IBM family class when panose says nothing.</summary>
    private static FallbackFamily? FamilyFromClassification(byte[] d, int os2)
    {
        var panose = os2 + 32;
        var familyType = d[panose];       // 2 = Latin text
        var serifStyle = d[panose + 1];
        var proportion = d[panose + 3];   // 9 = monospaced
        if (familyType == 2)
        {
            if (proportion == 9)
            {
                return FallbackFamily.Mono;
            }

            if (serifStyle is >= 2 and <= 10)
            {
                return FallbackFamily.Serif; // cove, square, thin, oval, exaggerated, triangle
            }

            if (serifStyle is >= 11 and <= 15)
            {
                return FallbackFamily.Sans; // normal sans, obtuse sans, perpendicular sans, flared, rounded
            }
        }

        var familyClass = d[os2 + 30]; // the high byte of sFamilyClass
        return familyClass switch
        {
            1 or 2 or 3 or 4 or 5 or 7 => FallbackFamily.Serif,
            8 => FallbackFamily.Sans,
            _ => null,
        };
    }

    private static ushort U16(byte[] d, int offset) => (ushort)((d[offset] << 8) | d[offset + 1]);

    private static uint U32(byte[] d, int offset)
        => ((uint)d[offset] << 24) | ((uint)d[offset + 1] << 16) | ((uint)d[offset + 2] << 8) | d[offset + 3];
}
