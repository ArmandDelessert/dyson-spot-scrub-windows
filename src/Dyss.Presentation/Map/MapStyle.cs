namespace Dyss.Presentation.Map;

/// <summary>A colour with its opacity, independent of any UI framework; each one turns it into its own brush.</summary>
public readonly record struct ArgbColor(byte A, byte R, byte G, byte B)
{
    /// <summary>Fully transparent white, which is what WPF's Colors.Transparent is: the pixels of unknown grid cells.</summary>
    public static readonly ArgbColor Transparent = new(0, 0xff, 0xff, 0xff);
    public static readonly ArgbColor White = FromRgb(0xff, 0xff, 0xff);
    public static readonly ArgbColor Black = FromRgb(0, 0, 0);

    public static ArgbColor FromRgb(byte r, byte g, byte b) => new(0xff, r, g, b);

    /// <summary>From 0xAARRGGBB.</summary>
    public static ArgbColor FromArgb(uint argb) => new((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);

    public ArgbColor WithAlpha(byte a) => this with { A = a };

    /// <summary>As 0xAARRGGBB in an int, the layout of a 32-bit BGRA pixel in memory.</summary>
    public int ToBgra32() => (A << 24) | (R << 16) | (G << 8) | B;
}

/// <summary>Colours of the map, one set per theme. Zone colours are generated (see <see cref="ZoneColor"/>), not listed, so any number of rooms gets visibly distinct hues.</summary>
public sealed record MapPalette(ArgbColor Background, ArgbColor Obstacle, ArgbColor LabelBackground, ArgbColor LabelText, ArgbColor Path, ArgbColor Furniture, double ZoneSaturation, double ZoneLightness, ArgbColor[] ActionColors)
{
    // Indexed by (int)CleanType: Vacuum, Mop, VacuumAndMop, VacuumThenMop. Used for the driven-path
    // segments where the robot was actively working (see MapScene.PathRuns); segments where it was
    // merely repositioning use Path instead.
    public static readonly MapPalette Dark = new(
        ArgbColor.FromRgb(0x1e, 0x1e, 0x22), ArgbColor.FromRgb(0x50, 0x50, 0x58),
        new(0xa0, 0, 0, 0), ArgbColor.White, new(0xc0, 0xff, 0xff, 0xff), new(0xa0, 0xff, 0xff, 0xff),
        ZoneSaturation: 0.55, ZoneLightness: 0.63,
        ActionColors: [ArgbColor.FromRgb(0x5a, 0xa9, 0xf0), ArgbColor.FromRgb(0x4d, 0xd6, 0xc4), ArgbColor.FromRgb(0xc4, 0x7a, 0xf0), ArgbColor.FromRgb(0xf0, 0xa8, 0x4d)]);

    public static readonly MapPalette Light = new(
        ArgbColor.FromRgb(0xf6, 0xf6, 0xf8), ArgbColor.FromRgb(0x60, 0x60, 0x68),
        new(0xd0, 0xff, 0xff, 0xff), ArgbColor.FromRgb(0x1a, 0x1a, 0x1e), new(0xd0, 0x20, 0x20, 0x30), new(0xa0, 0x20, 0x20, 0x30),
        ZoneSaturation: 0.65, ZoneLightness: 0.78,
        ActionColors: [ArgbColor.FromRgb(0x1f, 0x6f, 0xc9), ArgbColor.FromRgb(0x1a, 0x9e, 0x8c), ArgbColor.FromRgb(0x9a, 0x3c, 0xd6), ArgbColor.FromRgb(0xc9, 0x7a, 0x14)]);

    // The golden angle conjugate spreads hues around the wheel so that consecutive zone ids never
    // land near each other, unlike a short fixed palette cycling modulo its length (8 rooms used to
    // repeat the same colour).
    private const double GoldenAngleTurns = 0.6180339887498949;

    public ArgbColor ZoneColor(int zoneId)
    {
        var hue = Math.Abs(zoneId) * GoldenAngleTurns % 1.0 * 360.0;
        return FromHsl(hue, ZoneSaturation, ZoneLightness);
    }

    /// <summary>A third of the way from the background to the colour: the rooms an action is not about.</summary>
    public ArgbColor Dim(ArgbColor c) =>
        ArgbColor.FromRgb((byte)((c.R + Background.R * 2) / 3), (byte)((c.G + Background.G * 2) / 3), (byte)((c.B + Background.B * 2) / 3));

    private static ArgbColor FromHsl(double h, double s, double l)
    {
        var c = (1 - Math.Abs(2 * l - 1)) * s;
        var x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        var m = l - c / 2;
        var (r, g, b) = h switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        return ArgbColor.FromRgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }
}

/// <summary>The map's colours that are the same in both themes.</summary>
public static class MapColors
{
    /// <summary>What the map manager has chosen, and the zone about to be cleaned: a thick yellow outline.</summary>
    public static readonly ArgbColor Selection = ArgbColor.FromRgb(0xff, 0xd7, 0x00);
    /// <summary>The light wash inside the zone about to be cleaned, so it reads as a choice rather than as map data.</summary>
    public static readonly ArgbColor SpotFill = new(0x40, 0xff, 0xd7, 0x00);
    public static readonly ArgbColor ObstacleFill = ArgbColor.FromRgb(0xe0, 0xa0, 0x30);
    /// <summary>The stains: the green of the phone app's splash icon for "liquid".</summary>
    public static readonly ArgbColor Dirt = ArgbColor.FromRgb(0x3c, 0xb4, 0x3c);
    /// <summary>What is being aimed (a cut, a zone, a piece of furniture): red, dashed.</summary>
    public static readonly ArgbColor Pending = ArgbColor.FromRgb(0xe0, 0x30, 0x30);
    /// <summary>Restriction zones are filled with their outline's colour at this opacity.</summary>
    public const byte RestrictionFillAlpha = 0x50;
    /// <summary>Furniture is filled with the palette's furniture colour at this opacity.</summary>
    public const byte FurnitureFillAlpha = 0x30;
    /// <summary>The robot's grid lines are the label text colour at this opacity.</summary>
    public const byte GridLineAlpha = 0x38;

    /// <summary>
    /// The colour a kind of restriction zone is drawn in (REST behaviour), so the four read apart at
    /// a glance: red where the robot must not go, amber where it climbs, blue where it only mops,
    /// violet where it only vacuums. Kept the same in both themes; the legend in the map manager
    /// uses them too.
    /// </summary>
    public static ArgbColor Restriction(string? behavior) => behavior switch
    {
        "keepOut" => ArgbColor.FromRgb(0xe0, 0x50, 0x50),
        "climbObstacle" => ArgbColor.FromRgb(0xe8, 0xa0, 0x20),
        "brushBarOff" => ArgbColor.FromRgb(0x3c, 0x96, 0xe6),
        "noMop" => ArgbColor.FromRgb(0xa0, 0x6c, 0xe6),
        _ => ArgbColor.FromRgb(0x90, 0x90, 0x90),
    };
}
