using DyssCockpit.Core;
using DyssCockpit.Presentation.Map;

namespace DyssCockpit.Presentation.Tests;

/// <summary>
/// The arithmetic every renderer and the PNG export share: the grid lines, where the grid's image
/// goes, its pixels, and the zoom about the centre.
/// </summary>
public class MapGeometryTests
{
    private static readonly MapTransform TenPerMetre = MapTransform.Identity.Scaled(10, 10);

    // ---- Grid lines --------------------------------------------------------------------------

    [Fact]
    public void TheGridLinesCoverWhatIsOnScreenOnTheGridsOwnSteps()
    {
        // 35 × 25 px at 10 px a metre is 3.5 m × 2.5 m of world: lines at x 0 to 3, y 0 to 2.
        var lines = MapGeometry.GridLines(TenPerMetre, (0, 0, 1), new Size2(35, 25));

        Assert.Equal(7, lines.Count);
        Assert.Equal([0, 10, 20, 30], lines.Take(4).Select(l => l.From.X));
        Assert.All(lines.Take(4), l => Assert.Equal((0.0, 25.0), (l.From.Y, l.To.Y)));
        Assert.Equal([0, 10, 20], lines.Skip(4).Select(l => l.From.Y));
        Assert.All(lines.Skip(4), l => Assert.Equal((0.0, 35.0), (l.From.X, l.To.X)));
    }

    [Fact]
    public void TheLinesFollowTheGridsOrigin()
    {
        var lines = MapGeometry.GridLines(TenPerMetre, (0.25, 0, 1), new Size2(35, 25));

        Assert.Equal([2.5, 12.5, 22.5, 32.5], lines.Take(4).Select(l => l.From.X));
    }

    [Fact]
    public void ThereAreNoLinesWhileTheCellsAreTooCloseTogether()
    {
        // Half a metre at 10 px a metre is 5 px: the lines would only grey the map over.
        Assert.Empty(MapGeometry.GridLines(TenPerMetre, (0, 0, 0.5), new Size2(35, 25)));
        Assert.NotEmpty(MapGeometry.GridLines(TenPerMetre, (0, 0, 0.6), new Size2(35, 25)));
    }

    // ---- The grid's image ---------------------------------------------------------------------

    /// <summary>Four cells by two of half a metre, from (-1, -1) to (1, 0) in the world.</summary>
    private static readonly MapGrid Grid = new(new MapDimensions(4, 2, 0.5, -1, -1), [0, 0, 0, 0, 0, 0, 0, 0]);

    /// <summary>10 px a metre, y flipped, and a fraction of a pixel off.</summary>
    private static readonly MapTransform Screen = MapTransform.Identity.Scaled(10, -10).Translated(50.4, 50.4);

    [Fact]
    public void TheGridLandsOnWholePixels()
    {
        var placement = MapGeometry.PlaceGrid(Grid, Screen, 0);

        Assert.Equal(new Rect2(40, 50, 20, 10), placement.Destination);
        Assert.Equal(new Vec2(50, 55), placement.Centre);
    }

    [Theory]
    [InlineData(90)]
    [InlineData(270)]
    public void AQuarterTurnedGridIsDrawnWithItsSidesSwappedAboutTheSameCentre(int orientation)
    {
        var placement = MapGeometry.PlaceGrid(Grid, Screen, orientation);

        Assert.Equal(new Rect2(45, 45, 10, 20), placement.Destination);
        Assert.Equal(new Vec2(50, 55), placement.Centre);
        Assert.Equal(orientation, placement.Orientation);
    }

    [Fact]
    public void ThePixelsAreTopRowFirstWithRoomsObstaclesAndNothing()
    {
        // Bottom row: unknown, obstacle; top row: rooms 10 and 11.
        var grid = new MapGrid(new MapDimensions(2, 2, 1, 0, 0), [MapGrid.Unknown, MapGrid.Obstacle, 10, 11]);
        var palette = MapPalette.Dark;

        var pixels = MapGeometry.GridPixels(grid, null, palette);

        Assert.Equal([palette.ZoneColor(10).ToBgra32(), palette.ZoneColor(11).ToBgra32(), ArgbColor.Transparent.ToBgra32(), palette.Obstacle.ToBgra32()], pixels);
    }

    [Fact]
    public void RoomsLeftOutOfTheSelectionAreDimmed()
    {
        var grid = new MapGrid(new MapDimensions(2, 1, 1, 0, 0), [10, 11]);
        var palette = MapPalette.Light;

        var pixels = MapGeometry.GridPixels(grid, new HashSet<string> { "11" }, palette);

        Assert.Equal([palette.Dim(palette.ZoneColor(10)).ToBgra32(), palette.ZoneColor(11).ToBgra32()], pixels);
        // An empty selection dims nothing, like none at all.
        Assert.Equal(MapGeometry.GridPixels(grid, null, palette), MapGeometry.GridPixels(grid, new HashSet<string>(), palette));
    }

    [Fact]
    public void TwoSelectionsAreTheSameWhenTheyDimTheSameRooms()
    {
        Assert.True(MapGeometry.SameSelection(null, new HashSet<string>()));
        Assert.True(MapGeometry.SameSelection(new HashSet<string> { "10", "11" }, new HashSet<string> { "11", "10" }));
        Assert.False(MapGeometry.SameSelection(new HashSet<string> { "10" }, new HashSet<string> { "11" }));
        Assert.False(MapGeometry.SameSelection(new HashSet<string> { "10" }, null));
    }

    // ---- Sizes and the view --------------------------------------------------------------------

    [Fact]
    public void TheRoomsDotsAreACellWideButNeverUnderTwoPixels()
    {
        Assert.Equal(2, MapGeometry.VisitedDotRadius(TenPerMetre));
        Assert.Equal(12, MapGeometry.VisitedDotRadius(MapTransform.Identity.Scaled(100, 100)), 9);
    }

    [Fact]
    public void AZoomKeepsTheCentreOfTheViewInPlaceAndAPanMovesIt()
    {
        var zone = new MapZone("10", "Cuisine", null, null, [new Point(0, 0), new Point(4, 3)], null, null, null);
        var scene = new MapScene { Map = new PersistentMap("m", null, null, [zone], null, null, null) };
        var size = new Size2(400, 300);
        var fitted = MapGeometry.WorldToScreen(scene, size)!.Value;
        var centre = fitted.Inverse()!.Value.Transform(200, 150);

        var zoomed = MapGeometry.WorldToScreen(scene, size, zoom: 2)!.Value;
        var panned = MapGeometry.WorldToScreen(scene, size, zoom: 2, pan: new Vec2(10, -5))!.Value;

        Assert.Equal(2 * fitted.ScaleFactor, zoomed.ScaleFactor, 9);
        Assert.Equal(200, zoomed.Transform(centre).X, 9);
        Assert.Equal(150, zoomed.Transform(centre).Y, 9);
        Assert.Equal(210, panned.Transform(centre).X, 9);
        Assert.Equal(145, panned.Transform(centre).Y, 9);
    }

    [Fact]
    public void AnEmptySceneHasNoView() =>
        Assert.Null(MapGeometry.WorldToScreen(new MapScene(), new Size2(400, 300)));
}
