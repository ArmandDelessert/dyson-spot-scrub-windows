using System.Globalization;
using System.Numerics;
using Dyss.Core;
using Dyss.Presentation.Map;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Windows.Foundation;
using Windows.Graphics.DirectX;
using Windows.UI;
using CorePoint = Dyss.Core.Point;

namespace Dyss.App.Rendering;

/// <summary>
/// Draws a <see cref="MapScene"/> with Win2D, with the geometry and the colours
/// <see cref="MapGeometry"/> and <see cref="MapPalette"/> work out. Shared by the on-screen
/// <see cref="Controls.MapView"/> and the PNG export, so both agree. Everything is placed in screen
/// pixels: points go through the world-to-screen transform, lines keep their width whatever the zoom.
/// </summary>
internal static class MapRenderer
{
    private static readonly CanvasStrokeStyle RoundJoin = new() { LineJoin = CanvasLineJoin.Round };
    private static readonly CanvasStrokeStyle RoundEnds = new() { StartCap = CanvasCapStyle.Round, EndCap = CanvasCapStyle.Round };
    /// <summary>What is being aimed: dashed so it reads as a proposal, not as map data. Dashes are in line widths, as in WPF.</summary>
    private static readonly CanvasStrokeStyle Dashed = new() { CustomDashStyle = [4, 3], LineJoin = CanvasLineJoin.Round };

    private static readonly CanvasTextFormat LabelFont = new() { FontFamily = "Segoe UI", FontSize = 12, WordWrapping = CanvasWordWrapping.NoWrap };
    private static readonly CanvasTextFormat BadgeFont = new() { FontFamily = "Segoe UI", FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.Bold, WordWrapping = CanvasWordWrapping.NoWrap };
    private static readonly CanvasTextFormat MessageFont = new() { FontFamily = "Segoe UI", FontSize = 16, WordWrapping = CanvasWordWrapping.NoWrap };

    /// <summary>A text layout this wide never wraps nor clips the one-line texts drawn on the map.</summary>
    private const float Unbounded = 10_000;

    private static readonly Color Black = ArgbColor.Black.ToColor();
    private static readonly Color White = ArgbColor.White.ToColor();
    private static readonly Color Selection = MapColors.Selection.ToColor();
    private static readonly Color Pending = MapColors.Pending.ToColor();

    /// <summary>Draws the scene through <paramref name="worldToScreen"/> (see <see cref="MapGeometry.WorldToScreen"/>), or says there is no map when it is null.</summary>
    public static void Render(CanvasDrawingSession ds, MapScene scene, Size2 size, MapTransform? worldToScreen, MapPalette palette)
    {
        ds.Clear(palette.Background.ToColor());
        if (worldToScreen is not { } m)
        {
            DrawCentredText(ds, "Aucune carte", size, palette.LabelText.ToColor());
            return;
        }

        if (scene.Grid is { } grid)
            DrawGrid(ds, grid, m, scene.SelectedZoneIds, scene.Orientation, palette);
        else
            DrawVisitedPoints(ds, scene, m, palette);

        var furniturePen = palette.Furniture.ToColor();
        var furnitureFill = palette.Furniture.WithAlpha(MapColors.FurnitureFillAlpha).ToColor();
        foreach (var f in scene.ShowFurniture ? scene.Map?.Furniture ?? [] : [])
        {
            var chosen = f.Id == scene.SelectedFurnitureId;
            DrawPolygon(ds, f.Points, m, chosen ? Selection : furniturePen, chosen ? 3 : 1, furnitureFill);
        }

        foreach (var r in scene.Map?.Restrictions ?? [])
        {
            var colour = MapColors.Restriction(r.Behavior);
            var chosen = r.Id == scene.SelectedRestrictionId;
            DrawPolygon(ds, r.Points, m, chosen ? Selection : colour.ToColor(), chosen ? 3 : 2, colour.WithAlpha(MapColors.RestrictionFillAlpha).ToColor());
        }

        // The zone about to be cleaned: the selection yellow over a light wash, so it reads as a
        // choice rather than as map data.
        if (scene.SpotZone is { Count: > 2 } spot)
            DrawPolygon(ds, [.. spot], m, Selection, 3, MapColors.SpotFill.ToColor());

        if (scene.Path is { Count: > 1 } path)
            DrawActionPath(ds, scene, path, m, palette);

        var obstacleSize = (float)MapGeometry.ObstacleMarkerSize(m);
        foreach (var o in scene.Obstacles ?? [])
            DrawObstacleMarker(ds, m.Transform(o).ToVector2(), obstacleSize);

        foreach (var d in scene.DirtSpots ?? [])
        {
            var at = m.Transform(d.X, d.Y).ToVector2();
            ds.FillCircle(at, 5, MapColors.Dirt.ToColor());
            ds.DrawCircle(at, 5, Black, 1);
        }

        foreach (var z in scene.Map?.Zones ?? [])
        {
            if (z.NameLocation is not { } n) continue;
            var meta = scene.ZoneMetadata?.FirstOrDefault(zm => zm.Id == z.Id);
            var label = RoomTypeLabels.Resolve(meta?.Type ?? z.Type, meta?.Name ?? z.Name, z.Id);
            var at = m.Transform(n).ToVector2();
            var labelRect = DrawLabel(ds, label, at, palette);
            if (scene.ZoneOrder is { } order && order.TryGetValue(z.Id, out var rank))
                DrawBadge(ds, rank.ToString(CultureInfo.InvariantCulture), new Vector2((float)labelRect.Left - 12, at.Y));
        }
    }

    /// <summary>
    /// The driven path run by run (see <see cref="MapScene.PathRuns"/>): in the plain path colour
    /// where the robot was only repositioning, otherwise in the colour of the clean type of the
    /// room it was in.
    /// </summary>
    private static void DrawActionPath(CanvasDrawingSession ds, MapScene scene, IReadOnlyList<CorePoint> path, MapTransform m, MapPalette palette)
    {
        foreach (var run in scene.PathRuns)
        {
            if (run.To <= run.From) continue;
            ArgbColor colour;
            if (run.Action is not { } t)
            {
                if (!scene.ShowTravelPath) continue;
                colour = palette.Path;
            }
            else colour = palette.ActionColors[(int)t % palette.ActionColors.Length];

            using var builder = new CanvasPathBuilder(ds);
            builder.BeginFigure(m.Transform(path[run.From]).ToVector2());
            for (var j = run.From + 1; j <= run.To; j++) builder.AddLine(m.Transform(path[j]).ToVector2());
            builder.EndFigure(CanvasFigureLoop.Open);
            using var line = CanvasGeometry.CreatePath(builder);
            ds.DrawGeometry(line, colour.ToColor(), 2, RoundJoin);
        }
    }

    private static void DrawObstacleMarker(CanvasDrawingSession ds, Vector2 p, float half)
    {
        using var triangle = CanvasGeometry.CreatePolygon(ds,
        [
            new(p.X, p.Y - half * 7 / 6),
            new(p.X + half, p.Y + half * 5 / 6),
            new(p.X - half, p.Y + half * 5 / 6),
        ]);
        ds.FillGeometry(triangle, MapColors.ObstacleFill.ToColor());
        ds.DrawGeometry(triangle, Black, 1);
        // The exclamation mark: a stroke and a dot.
        ds.DrawLine(p.X, p.Y - half * 0.45f, p.X, p.Y + half * 0.2f, Black, 1.5f, RoundEnds);
        ds.FillCircle(p.X, p.Y + half * 0.5f, half * 0.1f, Black);
    }

    // ---- What is drawn over the map while something is aimed or dragged -------------

    /// <summary>Draws <see cref="MapInteraction.Overlay"/>: it belongs to an interaction rather than to the map, so it goes over the finished scene.</summary>
    public static void DrawOverlay(CanvasDrawingSession ds, MapOverlay overlay, MapTransform worldToScreen, Size2 size, MapPalette palette)
    {
        if (overlay.Grid is { } grid)
        {
            var gridColour = palette.LabelText.WithAlpha(MapColors.GridLineAlpha).ToColor();
            foreach (var (from, to) in MapGeometry.GridLines(worldToScreen, grid, size))
                ds.DrawLine(from.ToVector2(), to.ToVector2(), gridColour, 1);
        }
        if (overlay.SnapMarker is { } snap)
        {
            var at = snap.ToVector2();
            ds.DrawLine(at.X - 6, at.Y, at.X + 6, at.Y, Pending, 2, Dashed);
            ds.DrawLine(at.X, at.Y - 6, at.X, at.Y + 6, Pending, 2, Dashed);
        }
        if (overlay.CutFrom is { } cutFrom)
        {
            var from = cutFrom.ToVector2();
            if (overlay.CutTo is { } cutTo)
            {
                ds.DrawLine(from, cutTo.ToVector2(), Pending, 2, Dashed);
                ds.DrawCircle(cutTo.ToVector2(), 5, Pending, 2, Dashed);
            }
            ds.DrawCircle(from, 5, Pending, 2, Dashed);
        }
        if (overlay.RectangleFrom is { } corner)
        {
            ds.DrawCircle(corner.ToVector2(), 5, Pending, 2, Dashed);
            if (overlay.RectangleTo is { } other)
                ds.DrawRectangle(Rect2.FromCorners(corner, other).ToRect(), Pending, 2, Dashed);
        }
        if (overlay.PendingShape is { Count: > 2 } shape)
        {
            using var outline = CanvasGeometry.CreatePolygon(ds, [.. shape.Select(p => p.ToVector2())]);
            ds.DrawGeometry(outline, Pending, 2, Dashed);
        }
        foreach (var c in overlay.Handles ?? [])
        {
            var handle = new Rect(c.X - 5, c.Y - 5, 10, 10);
            ds.FillRectangle(handle, White);
            ds.DrawRectangle(handle, Black, 1.5f);
        }
    }

    // ---- The occupancy grid -------------------------------------------------------------

    // Panning and zooming redraw the map on every frame but never change the grid's own pixels,
    // only where they go, so the bitmap is kept until the grid, the selection, the colours or the
    // graphics device change.
    private static CanvasDevice? _cachedDevice;
    private static MapGrid? _cachedGrid;
    private static IReadOnlySet<string>? _cachedSelected;
    private static MapPalette? _cachedPalette;
    private static CanvasBitmap? _cachedBitmap;

    private static CanvasBitmap GridBitmap(CanvasDrawingSession ds, MapGrid grid, IReadOnlySet<string>? selected, MapPalette palette)
    {
        if (_cachedBitmap is not null && ReferenceEquals(_cachedDevice, ds.Device) && ReferenceEquals(_cachedGrid, grid)
            && ReferenceEquals(_cachedPalette, palette) && MapGeometry.SameSelection(_cachedSelected, selected))
            return _cachedBitmap;

        // Win2D's bitmaps hold premultiplied colours: a transparent cell has to be all zeros.
        var pixels = MapGeometry.GridPixels(grid, selected, palette);
        var bytes = new byte[pixels.Length * 4];
        for (var i = 0; i < pixels.Length; i++)
        {
            var p = pixels[i];
            if ((uint)p >> 24 == 0) continue;
            BitConverter.TryWriteBytes(bytes.AsSpan(i * 4), p);   // little-endian: B, G, R, A
        }
        _cachedBitmap?.Dispose();
        _cachedBitmap = CanvasBitmap.CreateFromBytes(ds, bytes, grid.Width, grid.Height, DirectXPixelFormat.B8G8R8A8UIntNormalized);
        _cachedDevice = ds.Device;
        _cachedGrid = grid;
        _cachedSelected = selected;
        _cachedPalette = palette;
        return _cachedBitmap;
    }

    private static void DrawGrid(CanvasDrawingSession ds, MapGrid grid, MapTransform m, IReadOnlySet<string>? selected, int orientation, MapPalette palette)
    {
        var bitmap = GridBitmap(ds, grid, selected, palette);
        var place = MapGeometry.PlaceGrid(grid, m, orientation);
        // Each 5 cm cell a sharp block rather than a smooth gradient, its edges too.
        var transform = ds.Transform;
        var antialiasing = ds.Antialiasing;
        ds.Antialiasing = CanvasAntialiasing.Aliased;
        if (place.Orientation != 0)
            ds.Transform = Matrix3x2.CreateRotation((float)(place.Orientation * Math.PI / 180), place.Centre.ToVector2()) * transform;
        ds.DrawImage(bitmap, place.Destination.ToRect(), bitmap.Bounds, 1, CanvasImageInterpolation.NearestNeighbor);
        ds.Transform = transform;
        ds.Antialiasing = antialiasing;
    }

    /// <summary>Without a grid (maps other than the current one), the visited points give the rooms' shape.</summary>
    private static void DrawVisitedPoints(CanvasDrawingSession ds, MapScene scene, MapTransform m, MapPalette palette)
    {
        var r = (float)MapGeometry.VisitedDotRadius(m);
        foreach (var z in scene.Map?.Zones ?? [])
        {
            if (z.Visited is not { Count: > 0 } pts || !int.TryParse(z.Id, CultureInfo.InvariantCulture, out var id)) continue;
            var dimmed = scene.SelectedZoneIds is { Count: > 0 } sel && !sel.Contains(z.Id);
            var colour = palette.ZoneColor(id);
            var fill = (dimmed ? palette.Dim(colour) : colour).ToColor();
            foreach (var p in pts) ds.FillCircle(m.Transform(p).ToVector2(), r, fill);
        }
    }

    private static void DrawPolygon(CanvasDrawingSession ds, List<CorePoint>? points, MapTransform m, Color stroke, float width, Color fill)
    {
        if (points is not { Count: > 2 }) return;
        using var polygon = CanvasGeometry.CreatePolygon(ds, [.. points.Select(p => m.Transform(p).ToVector2())]);
        ds.FillGeometry(polygon, fill);
        ds.DrawGeometry(polygon, stroke, width, RoundJoin);
    }

    private static Rect DrawLabel(CanvasDrawingSession ds, string text, Vector2 at, MapPalette palette)
    {
        using var layout = new CanvasTextLayout(ds, text, LabelFont, Unbounded, Unbounded);
        var (w, h) = ((float)layout.LayoutBounds.Width, (float)layout.LayoutBounds.Height);
        var rect = new Rect(at.X - w / 2 - 4, at.Y - h / 2 - 2, w + 8, h + 4);
        ds.FillRoundedRectangle(rect, 3, 3, palette.LabelBackground.ToColor());
        ds.DrawTextLayout(layout, (float)rect.X + 4, (float)rect.Y + 2, palette.LabelText.ToColor());
        return rect;
    }

    private static void DrawBadge(CanvasDrawingSession ds, string text, Vector2 at)
    {
        using var layout = new CanvasTextLayout(ds, text, BadgeFont, Unbounded, Unbounded);
        ds.FillCircle(at, 9, White);
        ds.DrawCircle(at, 9, Black, 1);
        ds.DrawTextLayout(layout, at.X - (float)layout.LayoutBounds.Width / 2, at.Y - (float)layout.LayoutBounds.Height / 2, Black);
    }

    private static void DrawCentredText(CanvasDrawingSession ds, string text, Size2 size, Color colour)
    {
        using var layout = new CanvasTextLayout(ds, text, MessageFont, Unbounded, Unbounded);
        ds.DrawTextLayout(layout, (float)(size.Width - layout.LayoutBounds.Width) / 2, (float)(size.Height - layout.LayoutBounds.Height) / 2, colour);
    }
}
