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
    public void PathRunsSplitWhereTheActionOrTheRoomChangesAndShareTheBoundaryPoint()
    {
        static ZoneMetadata Meta(string id, string cleanType) =>
            new(id, null, null, null, null, null, null, new ZoneSettings("auto", cleanType, "low", 1, 1, true));
        var scene = new MapScene
        {
            Map = MapWith(Zone("10", (0, 0)), Zone("11", (1, 0))),
            ZoneMetadata = [Meta("10", "vacuum"), Meta("11", "mop")],
            Path =
            [
                new Point(0, 0, Update: 0),      // driving to the first room
                new Point(0, 0.1, Update: 1),    // working in 10
                new Point(0.05, 0.1, Update: 1),
                new Point(1, 0, Update: 1),      // working in 11
                new Point(1, 0.1, Update: 0),    // repositioning again
                new Point(1, 0.2),               // no flag at all counts as repositioning
            ],
        };

        Assert.Equal(
            [new PathRun(0, 1, null), new PathRun(1, 3, CleanType.Vacuum), new PathRun(3, 4, CleanType.Mop), new PathRun(4, 5, null)],
            scene.PathRuns);
        Assert.Same(scene.PathRuns, scene.PathRuns); // computed once
        Assert.Empty(new MapScene { Path = [new Point(0, 0)] }.PathRuns);
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

    [Fact]
    public void TheGridToSnapToComesFromTheOccupancyGridOrElseTheStoredDimensions()
    {
        var dims = new MapDimensions(2, 2, 0.05, -5, -9);
        var map = new PersistentMap("1", 0, dims, [], null, null, null);

        Assert.Equal((-5.0, -9.0, 0.05), new MapScene { Map = map }.GridSpec);
        Assert.Equal((-1.0, -2.0, 0.1), new MapScene { Map = map, Grid = new MapGrid(new MapDimensions(1, 1, 0.1, -1, -2), [0]) }.GridSpec);
        Assert.Null(new MapScene().GridSpec);
    }
    [Fact]
    public void AMapTurnedAQuarterClockwiseHasItsTopOnTheRight()
    {
        var world = new System.Windows.Rect(0, 0, 4, 2);
        var size = new System.Windows.Size(400, 400);
        var top = new System.Windows.Point(2, 2);
        var bottom = new System.Windows.Point(2, 0);

        var upright = MapRenderer.FitTransform(world, size);
        Assert.True(upright.Transform(top).Y < upright.Transform(bottom).Y);

        var turned = MapRenderer.FitTransform(world, size, orientation: 90);
        Assert.True(turned.Transform(top).X > turned.Transform(bottom).X);
        // Still fitted: the whole map on screen, the long side now vertical.
        var corners = new[] { new System.Windows.Point(0, 0), new System.Windows.Point(4, 2) }.Select(turned.Transform).ToList();
        Assert.All(corners, c => Assert.InRange(c.X, 0, 400));
        Assert.True(Math.Abs(corners[0].Y - corners[1].Y) > Math.Abs(corners[0].X - corners[1].X));
    }

    [Fact]
    public void OnlyTheFourQuarterTurnsAreTakenFromTheMap()
    {
        static MapScene With(int? o) => new() { Map = new PersistentMap("1", o, null, [], null, null, null) };

        Assert.Equal(90, With(90).Orientation);
        Assert.Equal(270, With(270).Orientation);
        Assert.Equal(0, With(null).Orientation);
        Assert.Equal(0, With(45).Orientation);
    }

    [Fact]
    public void TheResizeArrowFollowsTheCornerAsItSitsOnScreen()
    {
        // Screen y points down. Top-right of its opposite, as in a map drawn with y up: ↗↙.
        Assert.False(MyDyson.App.Controls.MapView.PullsAlongMainDiagonal(new(100, 20), new(20, 100)));
        Assert.False(MyDyson.App.Controls.MapView.PullsAlongMainDiagonal(new(20, 100), new(100, 20)));
        // Bottom-right or top-left: ↖↘.
        Assert.True(MyDyson.App.Controls.MapView.PullsAlongMainDiagonal(new(100, 100), new(20, 20)));
        Assert.True(MyDyson.App.Controls.MapView.PullsAlongMainDiagonal(new(20, 20), new(100, 100)));
    }
}
