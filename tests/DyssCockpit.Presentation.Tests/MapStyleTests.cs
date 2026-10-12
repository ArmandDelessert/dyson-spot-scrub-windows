using DyssCockpit.Presentation.Map;

namespace DyssCockpit.Presentation.Tests;

/// <summary>The map's colours: how they are packed into pixels, and the rooms' generated hues.</summary>
public class MapStyleTests
{
    [Fact]
    public void AColourIsPackedAsAnAlphaRedGreenBlueInteger()
    {
        var c = ArgbColor.FromArgb(0x80112233);

        Assert.Equal(new ArgbColor(0x80, 0x11, 0x22, 0x33), c);
        Assert.Equal(unchecked((int)0x80112233), c.ToBgra32());
        Assert.Equal(0x00FFFFFF, ArgbColor.Transparent.ToBgra32());
        Assert.Equal(new ArgbColor(0x40, 0x11, 0x22, 0x33), c.WithAlpha(0x40));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NeighbouringRoomsGetColoursOfTheirOwn(bool dark)
    {
        var palette = dark ? MapPalette.Dark : MapPalette.Light;

        var colours = Enumerable.Range(10, 12).Select(palette.ZoneColor).ToList();

        Assert.Equal(colours.Count, colours.Distinct().Count());
        Assert.All(colours, c => Assert.Equal(0xFF, c.A));
        Assert.Equal(palette.ZoneColor(12), palette.ZoneColor(12));
        Assert.Equal(palette.ZoneColor(12), palette.ZoneColor(-12));
    }

    [Fact]
    public void TheFirstHueIsRed()
    {
        var c = MapPalette.Dark.ZoneColor(0);

        Assert.True(c.R > c.G, $"{c}");
        Assert.Equal(c.G, c.B);
    }

    [Fact]
    public void ADimmedColourIsAThirdOfTheWayFromTheBackground()
    {
        // Dark's background is 1E 1E 22: (255 + 2 × 30) / 3 and (255 + 2 × 34) / 3.
        Assert.Equal(ArgbColor.FromRgb(105, 105, 107), MapPalette.Dark.Dim(ArgbColor.White));
    }

    [Fact]
    public void EachKindOfRestrictionHasItsColourAndAnUnknownOneIsGrey()
    {
        string[] kinds = ["keepOut", "climbObstacle", "brushBarOff", "noMop"];

        var colours = kinds.Select(MapColors.Restriction).ToList();

        Assert.Equal(kinds.Length, colours.Distinct().Count());
        Assert.DoesNotContain(ArgbColor.FromRgb(0x90, 0x90, 0x90), colours);
        Assert.Equal(ArgbColor.FromRgb(0x90, 0x90, 0x90), MapColors.Restriction("somethingNew"));
        Assert.Equal(ArgbColor.FromRgb(0x90, 0x90, 0x90), MapColors.Restriction(null));
    }
}
