using System.Globalization;
using Dyss.Core;

namespace Dyss.Presentation.Map;

/// <summary>Where the occupancy grid's image goes on screen: drawn unturned in <see cref="Destination"/>, then turned <see cref="Orientation"/> degrees about <see cref="Centre"/>.</summary>
public readonly record struct GridPlacement(Rect2 Destination, Vec2 Centre, int Orientation);

/// <summary>
/// The arithmetic of drawing a map, shared by every renderer and by the PNG export: how the world
/// fits the screen, how big the markers are, where the grid goes and what its pixels are. World
/// coordinates are metres relative to the dock with y pointing up; the screen has y pointing down,
/// so the transform flips it.
/// </summary>
public static class MapGeometry
{
    /// <summary>
    /// Uniform world-to-screen transform that fits the bounds into the size with a margin, the map
    /// turned <paramref name="orientation"/> degrees clockwise on screen — the map's own
    /// "orientation", set by the phone's rotate button or the map manager.
    /// </summary>
    public static MapTransform FitTransform(Rect2 world, Size2 size, double margin = 16, int orientation = 0)
    {
        // y up in the world, down on screen; then the turn, which with y down is clockwise.
        var turn = MapTransform.Identity.Scaled(1, -1).Rotated(orientation);
        var corners = new[] { world.TopLeft, world.TopRight, world.BottomLeft, world.BottomRight }.Select(turn.Transform).ToList();
        var (minX, maxX, minY, maxY) = (corners.Min(p => p.X), corners.Max(p => p.X), corners.Min(p => p.Y), corners.Max(p => p.Y));

        var sx = (size.Width - 2 * margin) / (maxX - minX);
        var sy = (size.Height - 2 * margin) / (maxY - minY);
        var s = Math.Max(1e-6, Math.Min(sx, sy));
        var ox = margin + (size.Width - 2 * margin - (maxX - minX) * s) / 2;
        var oy = margin + (size.Height - 2 * margin - (maxY - minY) * s) / 2;
        return turn.Translated(-minX, -minY).Scaled(s, s).Translated(ox, oy);
    }

    /// <summary>
    /// The scene fitted to the size, then zoomed about the centre and panned, both in screen pixels.
    /// Null when the scene has nothing to show.
    /// </summary>
    public static MapTransform? WorldToScreen(MapScene scene, Size2 size, double zoom = 1, Vec2 pan = default)
    {
        if (scene.WorldBounds() is not { } bounds) return null;
        var m = FitTransform(bounds, size, orientation: scene.Orientation);
        if (zoom != 1 || pan != default)
            m = m.ScaledAt(zoom, zoom, size.Width / 2, size.Height / 2).Translated(pan.X, pan.Y);
        return m;
    }

    /// <summary>
    /// Half the width of an obstacle's warning triangle, in pixels: 8 cm on the floor, so it grows
    /// as the map is zoomed in, but never below 8 pixels nor above 16.
    /// </summary>
    public static double ObstacleMarkerSize(MapTransform worldToScreen) =>
        Math.Clamp(0.08 * worldToScreen.ScaleFactor, 8, 16);

    /// <summary>The dots that give a room its shape on maps without a grid: cell-sized, however the map is turned, and never under 2 pixels.</summary>
    public static double VisitedDotRadius(MapTransform worldToScreen) => Math.Max(2, 0.12 * worldToScreen.ScaleFactor);

    /// <summary>
    /// The robot's cell grid over the visible part of the map, as screen segments. None while the
    /// cells are under 6 px apart — at the usual zoom a 5 cm cell is a pixel or two, and the lines
    /// would only grey the map over. The lines are world lines put through the transform, so they
    /// follow a turned map too.
    /// </summary>
    public static IReadOnlyList<(Vec2 From, Vec2 To)> GridLines(MapTransform m, (double X0, double Y0, double Step) grid, Size2 size)
    {
        var stepPx = grid.Step * m.ScaleFactor;
        if (stepPx < 6 || m.Inverse() is not { } inverse) return [];
        var seen = new[] { new Vec2(0, 0), new Vec2(size.Width, 0), new Vec2(0, size.Height), new Vec2(size.Width, size.Height) }.Select(inverse.Transform).ToList();
        var (minX, maxX, minY, maxY) = (seen.Min(p => p.X), seen.Max(p => p.X), seen.Min(p => p.Y), seen.Max(p => p.Y));

        var lines = new List<(Vec2, Vec2)>();
        for (var k = Math.Ceiling((minX - grid.X0) / grid.Step); k <= Math.Floor((maxX - grid.X0) / grid.Step); k++)
        {
            var x = grid.X0 + k * grid.Step;
            lines.Add((m.Transform(x, minY), m.Transform(x, maxY)));
        }
        for (var k = Math.Ceiling((minY - grid.Y0) / grid.Step); k <= Math.Floor((maxY - grid.Y0) / grid.Step); k++)
        {
            var y = grid.Y0 + k * grid.Step;
            lines.Add((m.Transform(minX, y), m.Transform(maxX, y)));
        }
        return lines;
    }

    /// <summary>
    /// Where the grid's image lands. A destination at a fractional pixel position gets its edges
    /// blended against the background whatever the scaling, so it is snapped to whole pixels. A
    /// turned map still covers an upright rectangle on screen, quarter turns being the only ones
    /// there are: the image is drawn unturned, its sides swapped for 90 and 270, and turned about
    /// the centre of that rectangle.
    /// </summary>
    public static GridPlacement PlaceGrid(MapGrid grid, MapTransform m, int orientation)
    {
        var world = Rect2.FromCorners(new Vec2(grid.OffsetX, grid.OffsetY),
                                      new Vec2(grid.OffsetX + grid.Width * grid.Resolution, grid.OffsetY + grid.Height * grid.Resolution));
        var c0 = m.Transform(world.Left, world.Bottom);
        var c1 = m.Transform(world.Right, world.Top);
        var onScreen = Rect2.FromCorners(new Vec2(Math.Round(c0.X), Math.Round(c0.Y)), new Vec2(Math.Round(c1.X), Math.Round(c1.Y)));
        var quarter = orientation is 90 or 270;
        var (w, h) = quarter ? (onScreen.Height, onScreen.Width) : (onScreen.Width, onScreen.Height);
        var centre = new Vec2(onScreen.X + onScreen.Width / 2, onScreen.Y + onScreen.Height / 2);
        var destination = Rect2.FromCorners(new Vec2(centre.X - w / 2, centre.Y - h / 2), new Vec2(centre.X + w / 2, centre.Y + h / 2));
        return new GridPlacement(destination, centre, orientation);
    }

    /// <summary>
    /// The grid as 32-bit BGRA pixels (0xAARRGGBB per int), one per cell, top row first: unknown
    /// cells transparent, obstacles in the palette's colour, rooms in theirs — dimmed when some
    /// rooms are selected and this one is not.
    /// </summary>
    public static int[] GridPixels(MapGrid grid, IReadOnlySet<string>? selected, MapPalette palette)
    {
        var pixels = new int[grid.Width * grid.Height];
        for (var cy = 0; cy < grid.Height; cy++)
        {
            var row = (grid.Height - 1 - cy) * grid.Width;   // bitmap row 0 is the top, grid row 0 the bottom
            for (var cx = 0; cx < grid.Width; cx++)
            {
                var v = grid[cx, cy];
                ArgbColor c;
                if (v == MapGrid.Unknown) c = ArgbColor.Transparent;
                else if (v == MapGrid.Obstacle) c = palette.Obstacle;
                else
                {
                    c = palette.ZoneColor(v);
                    if (selected is { Count: > 0 } && !selected.Contains(v.ToString(CultureInfo.InvariantCulture)))
                        c = palette.Dim(c);
                }
                pixels[row + cx] = c.ToBgra32();
            }
        }
        return pixels;
    }

    /// <summary>Whether two selections dim the same rooms. An empty set and a null set both mean "nothing dimmed", so they compare equal.</summary>
    public static bool SameSelection(IReadOnlySet<string>? a, IReadOnlySet<string>? b)
    {
        var aEmpty = a is not { Count: > 0 };
        var bEmpty = b is not { Count: > 0 };
        if (aEmpty || bEmpty) return aEmpty == bEmpty;
        return a!.SetEquals(b!);
    }
}
