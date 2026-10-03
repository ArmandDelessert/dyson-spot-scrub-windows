using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Dyss.Core;
using Dyss.Presentation.Map;
using Point = System.Windows.Point;
using CorePoint = Dyss.Core.Point;

namespace Dyss.App.Rendering;

/// <summary>
/// Draws a <see cref="MapScene"/> onto a DrawingContext, with the geometry and the colours
/// <see cref="MapGeometry"/> and <see cref="MapPalette"/> work out. Shared by the on-screen control
/// and the PNG export, so both agree.
/// </summary>
public static class MapRenderer
{
    /// <summary>The colours of the current theme; see <see cref="Services.ThemeService"/>.</summary>
    public static MapPalette Palette { get; set; } = MapPalette.Dark;

    /// <summary>
    /// Frozen brushes and pens, built once per palette instead of on every frame: a new
    /// SolidColorBrush per draw call is cheap individually but adds up over the hundreds of calls
    /// a pan gesture triggers per second. Frozen objects are also cheaper for WPF to render.
    /// </summary>
    private sealed class Resources
    {
        public readonly Brush Background;
        public readonly Brush LabelText;
        public readonly Brush LabelBackground;
        public readonly Pen FurniturePen;
        public readonly Brush FurnitureFill;
        public readonly Pen GridPen;
        public readonly Pen PathPen;
        public readonly Pen[] ActionPens;   // indexed by (int)CleanType, like MapPalette.ActionColors
        private readonly MapPalette _palette;
        private readonly Dictionary<(int Id, bool Dimmed), Brush> _zoneBrushes = [];

        /// <summary>One colour per kind of restriction zone; see <see cref="MapColors.Restriction"/>.</summary>
        public static readonly Dictionary<string, (Pen Pen, Brush Fill)> Restrictions = new[] { "keepOut", "climbObstacle", "brushBarOff", "noMop" }
            .ToDictionary(b => b, b => RestrictionStyle(MapColors.Restriction(b)));
        public static readonly (Pen Pen, Brush Fill) UnknownRestriction = RestrictionStyle(MapColors.Restriction(null));
        public static readonly Pen HandlePen = Frozen(new Pen(Brushes.Black, 1.5));
        public static readonly Brush SpotFill = Solid(MapColors.SpotFill);
        /// <summary>What the map manager has chosen: a thick outline over whatever it is.</summary>
        public static readonly Pen SelectionPen = Frozen(new Pen(Solid(MapColors.Selection), 3) { LineJoin = PenLineJoin.Round });

        private static (Pen, Brush) RestrictionStyle(ArgbColor c) =>
            (Frozen(new Pen(Solid(c), 2)), Solid(c.WithAlpha(MapColors.RestrictionFillAlpha)));
        public static readonly Brush DirtFill = Solid(MapColors.Dirt);
        public static readonly Brush ObstacleFill = Solid(MapColors.ObstacleFill);
        public static readonly Pen ObstacleMark = Frozen(new Pen(Brushes.Black, 1.5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });
        public static readonly Pen BlackPen = Frozen(new Pen(Brushes.Black, 1));
        /// <summary>The cut being aimed while splitting a room: dashed so it reads as a proposal, not as map data.</summary>
        public static readonly Pen CutPen = Frozen(new Pen(Solid(MapColors.Pending), 2)
        {
            DashStyle = new DashStyle([4, 3], 0),
            LineJoin = PenLineJoin.Round,
        });
        public static readonly Typeface LabelFont = new("Segoe UI");
        public static readonly Typeface BadgeFont = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

        public Resources(MapPalette p)
        {
            _palette = p;
            Background = Solid(p.Background);
            LabelText = Solid(p.LabelText);
            LabelBackground = Solid(p.LabelBackground);
            FurniturePen = Frozen(new Pen(Solid(p.Furniture), 1));
            FurnitureFill = Solid(p.Furniture.WithAlpha(MapColors.FurnitureFillAlpha));
            GridPen = Frozen(new Pen(Solid(p.LabelText.WithAlpha(MapColors.GridLineAlpha)), 1));
            PathPen = LinePen(p.Path);
            ActionPens = [.. p.ActionColors.Select(LinePen)];
        }

        public Brush ZoneBrush(int zoneId, bool dimmed)
        {
            if (!_zoneBrushes.TryGetValue((zoneId, dimmed), out var brush))
            {
                var c = _palette.ZoneColor(zoneId);
                _zoneBrushes[(zoneId, dimmed)] = brush = Solid(dimmed ? _palette.Dim(c) : c);
            }
            return brush;
        }

        private static Pen LinePen(ArgbColor c) => Frozen(new Pen(Solid(c), 2) { LineJoin = PenLineJoin.Round });

        private static SolidColorBrush Solid(ArgbColor c) => Frozen(new SolidColorBrush(c.ToWpf()));

        private static T Frozen<T>(T freezable) where T : Freezable
        {
            freezable.Freeze();
            return freezable;
        }
    }

    private static Resources? _resources;
    private static MapPalette? _resourcesPalette;

    private static Resources Current
    {
        get
        {
            if (_resources is null || !ReferenceEquals(_resourcesPalette, Palette))
            {
                _resources = new Resources(Palette);
                _resourcesPalette = Palette;
            }
            return _resources;
        }
    }

    /// <summary>Draws the scene through <paramref name="worldToScreen"/> (see <see cref="MapGeometry.WorldToScreen"/>), or says there is no map when it is null.</summary>
    public static void Render(DrawingContext dc, MapScene scene, Size size, MapTransform? worldToScreen)
    {
        var res = Current;
        dc.DrawRectangle(res.Background, null, new Rect(size));
        if (worldToScreen is not { } t)
        {
            DrawCentredText(dc, "Aucune carte", size);
            return;
        }
        var m = t.ToWpf();

        if (scene.Grid is { } grid)
            DrawGrid(dc, grid, t, scene.SelectedZoneIds, scene.Orientation);
        else
            DrawVisitedPoints(dc, scene, t);

        foreach (var f in scene.ShowFurniture ? scene.Map?.Furniture ?? [] : [])
            DrawPolygon(dc, f.Points, m, f.Id == scene.SelectedFurnitureId ? Resources.SelectionPen : res.FurniturePen, res.FurnitureFill);

        foreach (var r in scene.Map?.Restrictions ?? [])
        {
            var (pen, fill) = r.Behavior is { } b && Resources.Restrictions.TryGetValue(b, out var style) ? style : Resources.UnknownRestriction;
            DrawPolygon(dc, r.Points, m, r.Id == scene.SelectedRestrictionId ? Resources.SelectionPen : pen, fill);
        }

        // The zone about to be cleaned: the selection yellow over a light wash, so it reads as a
        // choice rather than as map data.
        if (scene.SpotZone is { Count: > 2 } spot)
            DrawPolygon(dc, [.. spot], m, Resources.SelectionPen, Resources.SpotFill);

        if (scene.Path is { Count: > 1 } path)
            DrawActionPath(dc, res, scene, path, m);

        var obstacleSize = MapGeometry.ObstacleMarkerSize(t);
        foreach (var o in scene.Obstacles ?? [])
            DrawObstacleMarker(dc, m.Transform(new Point(o.X, o.Y)), obstacleSize);

        foreach (var d in scene.DirtSpots ?? [])
            DrawDirtMarker(dc, m.Transform(new Point(d.X, d.Y)));

        foreach (var z in scene.Map?.Zones ?? [])
        {
            if (z.NameLocation is not { } n) continue;
            var meta = scene.ZoneMetadata?.FirstOrDefault(zm => zm.Id == z.Id);
            var label = RoomTypeLabels.Resolve(meta?.Type ?? z.Type, meta?.Name ?? z.Name, z.Id);
            var at = m.Transform(new Point(n.X, n.Y));
            var labelRect = DrawLabel(dc, label, at);
            if (scene.ZoneOrder is { } order && order.TryGetValue(z.Id, out var rank))
                DrawBadge(dc, rank.ToString(CultureInfo.InvariantCulture), new Point(labelRect.Left - 12, at.Y));
        }
    }

    /// <summary>
    /// Draws the driven path run by run (see <see cref="MapScene.PathRuns"/>): in the plain path
    /// colour where the robot was only repositioning (point's "update" is 0 or missing, e.g. path
    /// data with no such field), otherwise in the colour of the clean type of the room it was in.
    /// </summary>
    private static void DrawActionPath(DrawingContext dc, Resources res, MapScene scene, IReadOnlyList<CorePoint> path, Matrix m)
    {
        foreach (var run in scene.PathRuns)
        {
            if (run.Action is not { } t)
            {
                if (!scene.ShowTravelPath) continue;
                DrawPathRun(dc, path, run.From, run.To, res.PathPen, m);
                continue;
            }
            DrawPathRun(dc, path, run.From, run.To, res.ActionPens[(int)t % res.ActionPens.Length], m);
        }
    }

    /// <summary>Draws points [from, to] (inclusive) as one polyline; a single point has nothing to join, so it's skipped.</summary>
    private static void DrawPathRun(DrawingContext dc, IReadOnlyList<CorePoint> path, int from, int to, Pen pen, Matrix m)
    {
        if (to <= from) return;
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.BeginFigure(m.Transform(new Point(path[from].X, path[from].Y)), false, false);
            for (var j = from + 1; j <= to; j++)
                g.LineTo(m.Transform(new Point(path[j].X, path[j].Y)), true, false);
        }
        geo.Freeze();
        dc.DrawGeometry(null, pen, geo);
    }

    private static void DrawObstacleMarker(DrawingContext dc, Point p, double half)
    {
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.BeginFigure(new Point(p.X, p.Y - half * 7 / 6), true, true);
            g.LineTo(new Point(p.X + half, p.Y + half * 5 / 6), true, true);
            g.LineTo(new Point(p.X - half, p.Y + half * 5 / 6), true, true);
        }
        geo.Freeze();
        dc.DrawGeometry(Resources.ObstacleFill, Resources.BlackPen, geo);
        // The exclamation mark: a stroke and a dot.
        dc.DrawLine(Resources.ObstacleMark, new Point(p.X, p.Y - half * 0.45), new Point(p.X, p.Y + half * 0.2));
        dc.DrawEllipse(Brushes.Black, null, new Point(p.X, p.Y + half * 0.5), half * 0.1, half * 0.1);
    }

    /// <summary>
    /// A plain green dot, matching the colour of the app's own splash icon for "liquid" (the only
    /// stain type confirmed so far; the app shows several distinct icons by type, but the others
    /// haven't been observed in a capture yet, so there's nothing to distinguish them by here).
    /// </summary>
    private static void DrawDirtMarker(DrawingContext dc, Point p) =>
        dc.DrawEllipse(Resources.DirtFill, Resources.BlackPen, p, 5, 5);

    // ---- What is drawn over the map while something is aimed or dragged -------------

    /// <summary>Draws <see cref="MapInteraction.Overlay"/>: it belongs to an interaction rather than to the map, so it goes over the finished scene.</summary>
    public static void DrawOverlay(DrawingContext dc, MapOverlay overlay, MapTransform worldToScreen, Size size)
    {
        if (overlay.Grid is { } grid)
            DrawGridLines(dc, MapGeometry.GridLines(worldToScreen, grid, new Size2(size.Width, size.Height)));
        if (overlay.SnapMarker is { } snap)
            DrawSnapMarker(dc, snap.ToWpf());
        if (overlay.CutFrom is { } cut)
            DrawPendingCut(dc, cut.ToWpf(), overlay.CutTo?.ToWpf());
        if (overlay.RectangleFrom is { } corner)
            DrawPendingRectangle(dc, corner.ToWpf(), overlay.RectangleTo?.ToWpf());
        if (overlay.PendingShape is { } shape)
            DrawPendingShape(dc, [.. shape.Select(p => p.ToWpf())]);
        if (overlay.Handles is { } handles)
            DrawHandles(dc, [.. handles.Select(p => p.ToWpf())]);
    }

    /// <summary>The cut being aimed while splitting a room: the first end as a ring, and the line to the cursor once there is one.</summary>
    private static void DrawPendingCut(DrawingContext dc, Point from, Point? to)
    {
        if (to is { } end) dc.DrawLine(Resources.CutPen, from, end);
        dc.DrawEllipse(null, Resources.CutPen, from, 5, 5);
        if (to is { } e2) dc.DrawEllipse(null, Resources.CutPen, e2, 5, 5);
    }

    /// <summary>The zone being drawn: its first corner, then the upright rectangle to the cursor.</summary>
    private static void DrawPendingRectangle(DrawingContext dc, Point from, Point? to)
    {
        dc.DrawEllipse(null, Resources.CutPen, from, 5, 5);
        if (to is { } end) dc.DrawRectangle(null, Resources.CutPen, new Rect(from, end));
    }

    /// <summary>Where a piece of furniture would land, or where a dragged shape would: its outline.</summary>
    private static void DrawPendingShape(DrawingContext dc, IReadOnlyList<Point> corners)
    {
        if (corners.Count < 3) return;
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.BeginFigure(corners[0], false, true);
            for (var i = 1; i < corners.Count; i++) g.LineTo(corners[i], true, false);
        }
        geo.Freeze();
        dc.DrawGeometry(null, Resources.CutPen, geo);
    }

    /// <summary>The robot's cell grid over the visible part of the map.</summary>
    private static void DrawGridLines(DrawingContext dc, IReadOnlyList<(Vec2 From, Vec2 To)> lines)
    {
        if (lines.Count == 0) return;
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            foreach (var (from, to) in lines)
            {
                g.BeginFigure(from.ToWpf(), false, false);
                g.LineTo(to.ToWpf(), true, false);
            }
        }
        geo.Freeze();
        dc.DrawGeometry(null, Current.GridPen, geo);
    }

    /// <summary>A small cross where the pointer lands on the grid.</summary>
    private static void DrawSnapMarker(DrawingContext dc, Point at)
    {
        dc.DrawLine(Resources.CutPen, new Point(at.X - 6, at.Y), new Point(at.X + 6, at.Y));
        dc.DrawLine(Resources.CutPen, new Point(at.X, at.Y - 6), new Point(at.X, at.Y + 6));
    }

    /// <summary>Square grips on the corners of a shape that can be resized.</summary>
    private static void DrawHandles(DrawingContext dc, IReadOnlyList<Point> corners)
    {
        foreach (var c in corners)
            dc.DrawRectangle(Brushes.White, Resources.HandlePen, new Rect(c.X - 5, c.Y - 5, 10, 10));
    }

    // ---- The occupancy grid -------------------------------------------------------------

    // Panning/zooming re-renders every frame but never changes the grid's own pixels, only where
    // they're drawn: rebuilding an 84 000-cell bitmap on every single frame (as this used to do)
    // made dragging noticeably less smooth, touch manipulation especially, which reports move deltas
    // more eagerly than the mouse. Cached across calls since MapRenderer is already static/UI-thread-
    // only; invalidated only when the grid instance or the selected set actually changes.
    private static MapGrid? _cachedGrid;
    private static IReadOnlySet<string>? _cachedSelected;
    private static MapPalette? _cachedPalette;
    private static WriteableBitmap? _cachedGridBitmap;

    private static WriteableBitmap BuildGridBitmap(MapGrid grid, IReadOnlySet<string>? selected)
    {
        // The palette is part of the key: the pixels bake in zone and obstacle colours, and a theme
        // switch replaces the palette without touching the grid instance or the selection.
        if (_cachedGridBitmap is not null && ReferenceEquals(_cachedGrid, grid) && ReferenceEquals(_cachedPalette, Palette)
            && MapGeometry.SameSelection(_cachedSelected, selected))
            return _cachedGridBitmap;

        var bmp = new WriteableBitmap(grid.Width, grid.Height, 96, 96, PixelFormats.Bgra32, null);
        bmp.WritePixels(new Int32Rect(0, 0, grid.Width, grid.Height), MapGeometry.GridPixels(grid, selected, Palette), grid.Width * 4, 0);
        RenderOptions.SetBitmapScalingMode(bmp, BitmapScalingMode.NearestNeighbor); // before Freeze: a frozen bitmap is read-only
        bmp.Freeze();

        _cachedGrid = grid;
        _cachedSelected = selected;
        _cachedPalette = Palette;
        _cachedGridBitmap = bmp;
        return bmp;
    }

    private static void DrawGrid(DrawingContext dc, MapGrid grid, MapTransform m, IReadOnlySet<string>? selected, int orientation = 0)
    {
        var bmp = BuildGridBitmap(grid, selected);
        var place = MapGeometry.PlaceGrid(grid, m, orientation);

        // NearestNeighbor on the bitmap keeps each 5 cm cell a sharp block instead of a smooth
        // gradient, but WPF's compositor still anti-aliases the drawn image's own edges unless told
        // not to. EdgeMode is scoped to this DrawingGroup rather than set on the MapView itself, so
        // labels, the robot and the path stay anti-aliased as normal.
        var group = new DrawingGroup();
        RenderOptions.SetEdgeMode(group, EdgeMode.Aliased);
        RenderOptions.SetBitmapScalingMode(group, BitmapScalingMode.NearestNeighbor);
        using (var gdc = group.Open())
        {
            if (place.Orientation != 0) gdc.PushTransform(new RotateTransform(place.Orientation, place.Centre.X, place.Centre.Y));
            gdc.DrawImage(bmp, place.Destination.ToWpf());
            if (place.Orientation != 0) gdc.Pop();
        }
        group.Freeze();
        dc.DrawDrawing(group);
    }

    /// <summary>Without a grid (maps other than the current one), the visited points give the rooms' shape.</summary>
    private static void DrawVisitedPoints(DrawingContext dc, MapScene scene, MapTransform t)
    {
        var m = t.ToWpf();
        var r = MapGeometry.VisitedDotRadius(t);
        foreach (var z in scene.Map?.Zones ?? [])
        {
            if (z.Visited is not { Count: > 0 } pts || !int.TryParse(z.Id, out var id)) continue;
            var dimmed = scene.SelectedZoneIds is { Count: > 0 } sel && !sel.Contains(z.Id);
            var brush = Current.ZoneBrush(id, dimmed);
            foreach (var p in pts)
                dc.DrawEllipse(brush, null, m.Transform(new Point(p.X, p.Y)), r, r);
        }
    }

    private static void DrawPolygon(DrawingContext dc, List<CorePoint>? points, Matrix m, Pen pen, Brush fill)
    {
        if (points is not { Count: > 2 }) return;
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.BeginFigure(m.Transform(new Point(points[0].X, points[0].Y)), true, true);
            for (var i = 1; i < points.Count; i++)
                g.LineTo(m.Transform(new Point(points[i].X, points[i].Y)), true, false);
        }
        geo.Freeze();
        dc.DrawGeometry(fill, pen, geo);
    }

    private static Rect DrawLabel(DrawingContext dc, string text, Point at)
    {
        var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            Resources.LabelFont, 12, Current.LabelText, 1.0);
        var rect = new Rect(at.X - ft.Width / 2 - 4, at.Y - ft.Height / 2 - 2, ft.Width + 8, ft.Height + 4);
        dc.DrawRoundedRectangle(Current.LabelBackground, null, rect, 3, 3);
        dc.DrawText(ft, new Point(rect.X + 4, rect.Y + 2));
        return rect;
    }

    private static void DrawBadge(DrawingContext dc, string text, Point at)
    {
        var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            Resources.BadgeFont, 11, Brushes.Black, 1.0);
        dc.DrawEllipse(Brushes.White, Resources.BlackPen, at, 9, 9);
        dc.DrawText(ft, new Point(at.X - ft.Width / 2, at.Y - ft.Height / 2));
    }

    private static void DrawCentredText(DrawingContext dc, string text, Size size)
    {
        var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            Resources.LabelFont, 16, Current.LabelText, 1.0);
        dc.DrawText(ft, new Point((size.Width - ft.Width) / 2, (size.Height - ft.Height) / 2));
    }

    /// <summary>Renders the scene to a PNG file, for sharing or for checking the renderer without a window.</summary>
    public static void ExportPng(MapScene scene, int width, int height, string path)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var size = new Size(width, height);
            var m = MapGeometry.WorldToScreen(scene, new Size2(width, height));
            Render(dc, scene, size, m);
            RobotMarkers.Draw(dc, scene, m ?? MapTransform.Identity, 0);
        }
        var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(path);
        encoder.Save(fs);
    }
}
