namespace DyssCockpit.Core;

/// <summary>
/// Decoded occupancy grid of GET /v1/app/{serial}/live-maps/mapping.
///
/// Cell values: 0 unknown or outside, 255 obstacle, and otherwise the id of the zone the cell
/// belongs to (10, 11, 12… match the zone ids of the map). Established on 2026-09-19 by checking
/// that the visited points of each zone land on cells carrying that zone's id: row-major order,
/// x and y both increasing with the cell index, no flip.
/// </summary>
public sealed class MapGrid
{
    public const int Unknown = 0;
    public const int Obstacle = 255;

    private readonly int[] _cells;

    public int Width { get; }
    public int Height { get; }
    public double Resolution { get; }
    public double OffsetX { get; }
    public double OffsetY { get; }

    public MapGrid(MapDimensions dimensions, IReadOnlyList<int> cells)
    {
        if (cells.Count != dimensions.Width * dimensions.Height)
            throw new ArgumentException($"Grid has {cells.Count} cells, expected {dimensions.Width}x{dimensions.Height}.");
        Width = dimensions.Width;
        Height = dimensions.Height;
        Resolution = dimensions.Resolution;
        OffsetX = dimensions.OffsetX;
        OffsetY = dimensions.OffsetY;
        _cells = cells as int[] ?? cells.ToArray();
    }

    public static MapGrid? From(MappingMap map) =>
        map.Dimensions is { } d && map.MapData is { } cells ? new MapGrid(d, cells) : null;

    /// <summary>Value at a cell, or Unknown outside the grid.</summary>
    public int this[int cx, int cy] =>
        cx >= 0 && cx < Width && cy >= 0 && cy < Height ? _cells[cy * Width + cx] : Unknown;

    public bool IsObstacle(int cx, int cy) => this[cx, cy] == Obstacle;
    public bool IsFree(int cx, int cy) => this[cx, cy] is not (Unknown or Obstacle);
    public int? ZoneIdAt(int cx, int cy) => this[cx, cy] is var v && v is not (Unknown or Obstacle) ? v : null;

    /// <summary>World metres to cell indices (may fall outside the grid).</summary>
    public (int Cx, int Cy) ToCell(double x, double y) =>
        ((int)Math.Floor((x - OffsetX) / Resolution), (int)Math.Floor((y - OffsetY) / Resolution));

    /// <summary>Centre of a cell in world metres.</summary>
    public (double X, double Y) ToWorld(int cx, int cy) =>
        (OffsetX + (cx + 0.5) * Resolution, OffsetY + (cy + 0.5) * Resolution);

    public int? ZoneIdAtWorld(double x, double y)
    {
        var (cx, cy) = ToCell(x, y);
        return ZoneIdAt(cx, cy);
    }

    /// <summary>Bounding box, in cells, of everything that is not Unknown. Null for an empty grid.</summary>
    public (int MinCx, int MinCy, int MaxCx, int MaxCy)? KnownBounds()
    {
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        for (var cy = 0; cy < Height; cy++)
            for (var cx = 0; cx < Width; cx++)
                if (_cells[cy * Width + cx] != Unknown)
                {
                    if (cx < minX) minX = cx;
                    if (cy < minY) minY = cy;
                    if (cx > maxX) maxX = cx;
                    if (cy > maxY) maxY = cy;
                }
        return maxX < 0 ? null : (minX, minY, maxX, maxY);
    }
}
