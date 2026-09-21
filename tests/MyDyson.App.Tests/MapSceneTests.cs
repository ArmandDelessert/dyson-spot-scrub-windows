using MyDyson.App.Rendering;
using MyDyson.Core;

namespace MyDyson.App.Tests;

public class MapSceneTests
{
    private static MapZone Zone(string id, params (double X, double Y)[] visited) =>
        new(id, $"Zone {id}", null, null, [.. visited.Select(p => new Point(p.X, p.Y))], null, null, null);

    private static PersistentMap MapWith(params MapZone[] zones) => new("m", null, null, [.. zones], null, null, null);

    [Fact]
    public void BoundsCoverTheVisitedPointsWithHalfAMetreOfMargin()
    {
        var scene = new MapScene { Map = MapWith(Zone("10", (0, 0), (4, 3))) };

        var b = scene.WorldBounds()!.Value;

        Assert.Equal(-0.5, b.Left);
        Assert.Equal(-0.5, b.Top);
        Assert.Equal(4.5, b.Right);
        Assert.Equal(3.5, b.Bottom);
    }

    [Fact]
    public void ADockNearTheDataStretchesTheBoundsToIt()
    {
        var scene = new MapScene { Map = MapWith(Zone("10", (0, 0), (4, 3))), Dock = new DockLocation(-2, -1, 0) };

        var b = scene.WorldBounds()!.Value;

        Assert.Equal(-2.5, b.Left);
        Assert.Equal(-1.5, b.Top);
    }

    [Fact]
    public void ASentinelDockFarOutsideTheDataIsIgnored()
    {
        // Seen on maps with zone definitions but no completed run: dock at (1100, 1100). Taking it
        // into account would shrink the real floor plan to a few pixels.
        var scene = new MapScene { Map = MapWith(Zone("10", (0, 0), (4, 3))), Dock = new DockLocation(1100, 1100, 0) };

        var b = scene.WorldBounds()!.Value;

        Assert.Equal(4.5, b.Right);
        Assert.Equal(3.5, b.Bottom);
    }

    [Fact]
    public void TheDockAloneIsEnoughWhenThereIsNothingElse()
    {
        var scene = new MapScene { Dock = new DockLocation(1, 2, 0) };

        var b = scene.WorldBounds()!.Value;

        Assert.Equal(0.5, b.Left);
        Assert.Equal(1.5, b.Top);
        Assert.Equal(1, b.Width);
        Assert.Null(new MapScene().WorldBounds());
    }

    [Fact]
    public void TheGridWinsOverVectorDataWhenPresent()
    {
        // 2 x 2 cells of 1 m at the origin, only the top-right one known: bounds are that cell's
        // centre plus one cell of margin on each side, whatever the zones say.
        var grid = new MapGrid(new MapDimensions(2, 2, 1, 0, 0), [0, 0, 0, 10]);
        var scene = new MapScene { Grid = grid, Map = MapWith(Zone("10", (100, 100))) };

        var b = scene.WorldBounds()!.Value;

        Assert.Equal(0.5, b.Left);
        Assert.Equal(0.5, b.Top);
        Assert.Equal(2.5, b.Right);
        Assert.Equal(2.5, b.Bottom);
    }

    [Fact]
    public void ZoneLookupUsesTheGridWhenThereIsOneAndVisitedPointsOtherwise()
    {
        var grid = new MapGrid(new MapDimensions(2, 1, 1, 0, 0), [10, 11]);
        Assert.Equal("11", new MapScene { Grid = grid }.ZoneAt(1.5, 0.5));
        Assert.Null(new MapScene { Grid = grid }.ZoneAt(-1, 0));

        var vector = new MapScene { Map = MapWith(Zone("10", (0, 0)), Zone("11", (1, 0))) };
        Assert.Equal("11", vector.ZoneAt(0.8, 0.1));
        Assert.Null(vector.ZoneAt(0.5, 0.5)); // more than 30 cm from any visited point
    }
}
