using Dyss.Core;

namespace Dyss.Core.Tests;

public class MapGridTests
{
    // 4 x 3 cells of 0.5 m, origin at (-1, 2): row-major, x then y increasing with the index.
    //   y=2: [ 0, 10, 10, 255 ]
    //   y=1: [ 0, 11,  0,   0 ]
    //   y=0: [ 0,  0,  0,   0 ]
    private static readonly MapDimensions Dims = new(Width: 4, Height: 3, Resolution: 0.5, OffsetX: -1, OffsetY: 2);
    private static readonly int[] Cells =
    [
        0, 0, 0, 0,
        0, 11, 0, 0,
        0, 10, 10, 255,
    ];

    [Fact]
    public void CellsAreRowMajorWithZonesObstaclesAndUnknown()
    {
        var g = new MapGrid(Dims, Cells);

        Assert.Equal(10, g[1, 2]);
        Assert.Equal(11, g[1, 1]);
        Assert.True(g.IsObstacle(3, 2));
        Assert.False(g.IsFree(3, 2));
        Assert.True(g.IsFree(2, 2));
        Assert.Null(g.ZoneIdAt(0, 0));
        Assert.Null(g.ZoneIdAt(3, 2));
        Assert.Equal(11, g.ZoneIdAt(1, 1));
        // Outside the grid reads as Unknown rather than throwing.
        Assert.Equal(MapGrid.Unknown, g[-1, 0]);
        Assert.Equal(MapGrid.Unknown, g[0, 3]);
        Assert.False(g.IsObstacle(99, 99));
    }

    [Fact]
    public void WorldAndCellCoordinatesRoundTrip()
    {
        var g = new MapGrid(Dims, Cells);

        Assert.Equal((0, 0), g.ToCell(-1, 2));
        Assert.Equal((-1, -1), g.ToCell(-1.01, 1.99)); // just past the origin falls in the previous cell
        Assert.Equal((-0.25, 3.25), g.ToWorld(1, 2));
        for (var cy = 0; cy < Dims.Height; cy++)
            for (var cx = 0; cx < Dims.Width; cx++)
            {
                var (x, y) = g.ToWorld(cx, cy);
                Assert.Equal((cx, cy), g.ToCell(x, y));
            }
        Assert.Equal(10, g.ZoneIdAtWorld(-0.3, 3.2));
        Assert.Null(g.ZoneIdAtWorld(50, 50));
    }

    [Fact]
    public void KnownBoundsIgnoreUnknownCellsAndIncludeObstacles()
    {
        Assert.Equal((1, 1, 3, 2), new MapGrid(Dims, Cells).KnownBounds()!.Value);
        Assert.Null(new MapGrid(new MapDimensions(2, 2, 0.05, 0, 0), new int[4]).KnownBounds());
    }

    [Fact]
    public void RejectsACellCountThatDoesNotMatchTheDimensions()
    {
        Assert.Throws<ArgumentException>(() => new MapGrid(Dims, new int[11]));
    }

    [Fact]
    public void FromMappingMapNeedsBothDimensionsAndData()
    {
        Assert.Null(MapGrid.From(new MappingMap(null, null, [.. Cells])));
        Assert.Null(MapGrid.From(new MappingMap(Dims, null, null)));
        Assert.Equal(4, MapGrid.From(new MappingMap(Dims, null, [.. Cells]))!.Width);
    }
}
