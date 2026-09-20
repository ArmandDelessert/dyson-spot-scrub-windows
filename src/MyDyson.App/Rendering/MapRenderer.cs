using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyDyson.Core;
using Point = System.Windows.Point;
using CorePoint = MyDyson.Core.Point;

namespace MyDyson.App.Rendering;

/// <summary>Colours of the map, one set per theme. Zone colours are generated (see <see cref="MapRenderer.ZoneColor"/>), not listed, so any number of rooms gets visibly distinct hues.</summary>
public sealed record MapPalette(Color Background, Color Obstacle, Color LabelBackground, Color LabelText, Color Path, Color Furniture, double ZoneSaturation, double ZoneLightness, Color[] ActionColors)
{
    // Indexed by (int)CleanType: Vacuum, Mop, VacuumAndMop, VacuumThenMop. Used for the driven-path
    // segments where the robot was actively working (see MapRenderer.DrawActionPath); segments where
    // it was merely repositioning use Path instead, same as before this distinction existed.
    public static readonly MapPalette Dark = new(
        Color.FromRgb(0x1e, 0x1e, 0x22), Color.FromRgb(0x50, 0x50, 0x58),
        Color.FromArgb(0xa0, 0, 0, 0), Colors.White, Color.FromArgb(0xc0, 0xff, 0xff, 0xff), Color.FromArgb(0xa0, 0xff, 0xff, 0xff),
        ZoneSaturation: 0.55, ZoneLightness: 0.63,
        ActionColors: [Color.FromRgb(0x5a, 0xa9, 0xf0), Color.FromRgb(0x4d, 0xd6, 0xc4), Color.FromRgb(0xc4, 0x7a, 0xf0), Color.FromRgb(0xf0, 0xa8, 0x4d)]);

    public static readonly MapPalette Light = new(
        Color.FromRgb(0xf6, 0xf6, 0xf8), Color.FromRgb(0x60, 0x60, 0x68),
        Color.FromArgb(0xd0, 0xff, 0xff, 0xff), Color.FromRgb(0x1a, 0x1a, 0x1e), Color.FromArgb(0xd0, 0x20, 0x20, 0x30), Color.FromArgb(0xa0, 0x20, 0x20, 0x30),
        ZoneSaturation: 0.65, ZoneLightness: 0.78,
        ActionColors: [Color.FromRgb(0x1f, 0x6f, 0xc9), Color.FromRgb(0x1a, 0x9e, 0x8c), Color.FromRgb(0x9a, 0x3c, 0xd6), Color.FromRgb(0xc9, 0x7a, 0x14)]);
}

/// <summary>Everything the map view knows how to draw. All optional; missing layers are skipped.</summary>
public sealed class MapScene
{
    public MapGrid? Grid { get; init; }
    public PersistentMap? Map { get; init; }
    public IReadOnlyList<ZoneMetadata>? ZoneMetadata { get; init; }
    public RobotPosition? Robot { get; init; }
    public DockLocation? Dock { get; init; }
    public IReadOnlyList<CorePoint>? Path { get; init; }
    public IReadOnlyList<CorePoint>? Obstacles { get; init; }
    public IReadOnlySet<string>? SelectedZoneIds { get; init; }
    /// <summary>Zone id to its position in the clean order, shown as a badge.</summary>
    public IReadOnlyDictionary<string, int>? ZoneOrder { get; init; }

    /// <summary>World-space bounds (metres) of what is worth showing.</summary>
    public Rect? WorldBounds()
    {
        if (Grid is { } g && g.KnownBounds() is { } b)
        {
            var (x0, y0) = g.ToWorld(b.MinCx, b.MinCy);
            var (x1, y1) = g.ToWorld(b.MaxCx, b.MaxCy);
            return new Rect(new Point(x0 - g.Resolution, y0 - g.Resolution), new Point(x1 + g.Resolution, y1 + g.Resolution));
        }
        var pts = new List<CorePoint>();
        foreach (var z in Map?.Zones ?? [])
        {
            if (z.Visited is { } v) pts.AddRange(v);
            if (z.NameLocation is { } n) pts.Add(n);
        }
        if (Path is { } p) pts.AddRange(p);
        if (Obstacles is { } obs) pts.AddRange(obs);
        // The dock location is sometimes a sentinel far outside the real floor plan (observed:
        // (1100, 1100) on maps the robot has zone definitions for but has never actually mapped
        // a run on). Blindly including it would balloon the bounding box and shrink the real
        // geometry to a few pixels, so it only counts towards the bounds when it is plausibly
        // close to the rest of the data.
        if (Dock is { } d)
        {
            if (pts.Count == 0) pts.Add(new CorePoint(d.X, d.Y));
            else
            {
                var minX0 = pts.Min(q => q.X); var maxX0 = pts.Max(q => q.X);
                var minY0 = pts.Min(q => q.Y); var maxY0 = pts.Max(q => q.Y);
                var margin = Math.Max(Math.Max(maxX0 - minX0, maxY0 - minY0), 5);
                if (d.X >= minX0 - margin && d.X <= maxX0 + margin && d.Y >= minY0 - margin && d.Y <= maxY0 + margin)
                    pts.Add(new CorePoint(d.X, d.Y));
            }
        }
        if (pts.Count == 0) return null;
        var minX = pts.Min(p => p.X); var maxX = pts.Max(p => p.X);
        var minY = pts.Min(p => p.Y); var maxY = pts.Max(p => p.Y);
        return new Rect(new Point(minX - 0.5, minY - 0.5), new Point(maxX + 0.5, maxY + 0.5));
    }

    /// <summary>Zone under a world point: from the grid when there is one, else the nearest visited point within 30 cm.</summary>
    public string? ZoneAt(double x, double y)
    {
        if (Grid is { } g)
            return g.ZoneIdAtWorld(x, y)?.ToString(CultureInfo.InvariantCulture);
        string? best = null;
        var bestD = 0.3 * 0.3;
        foreach (var z in Map?.Zones ?? [])
            foreach (var p in z.Visited ?? [])
            {
                var d = (p.X - x) * (p.X - x) + (p.Y - y) * (p.Y - y);
                if (d < bestD) { bestD = d; best = z.Id; }
            }
        return best;
    }
}

/// <summary>
/// Draws a <see cref="MapScene"/> onto a DrawingContext. World coordinates are metres relative to
/// the dock with y pointing up; the screen has y pointing down, so the transform flips it.
/// Shared by the on-screen control and the PNG export, so both agree.
/// </summary>
public static class MapRenderer
{
    public static MapPalette Palette { get; set; } = MapPalette.Dark;

    // The golden angle conjugate spreads hues around the wheel so that consecutive zone ids never
    // land near each other, unlike a short fixed palette cycling modulo its length (8 rooms used to
    // repeat the same colour: see docs/protocole.md).
    private const double GoldenAngleTurns = 0.6180339887498949;

    public static Color ZoneColor(int zoneId)
    {
        var hue = Math.Abs(zoneId) * GoldenAngleTurns % 1.0 * 360.0;
        return FromHsl(hue, Palette.ZoneSaturation, Palette.ZoneLightness);
    }

    private static Color FromHsl(double h, double s, double l)
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
        return Color.FromRgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }

    /// <summary>Uniform world-to-screen transform that fits the bounds into the size with a margin.</summary>
    public static Matrix FitTransform(Rect world, Size size, double margin = 16)
    {
        var sx = (size.Width - 2 * margin) / world.Width;
        var sy = (size.Height - 2 * margin) / world.Height;
        var s = Math.Max(1e-6, Math.Min(sx, sy));
        var ox = margin + (size.Width - 2 * margin - world.Width * s) / 2;
        var oy = margin + (size.Height - 2 * margin - world.Height * s) / 2;
        var m = Matrix.Identity;
        m.Translate(-world.X, -world.Bottom);   // (minX, maxY) to the origin; Rect.Bottom is maxY with y up
        m.Scale(s, -s);
        m.Translate(ox, oy);
        return m;
    }

    /// <summary>Renders with an extra zoom about the viewport centre and a pan, both in screen pixels.</summary>
    public static void Render(DrawingContext dc, MapScene scene, Size size, out Matrix worldToScreen, double zoom = 1, Vector pan = default)
    {
        var bg = new SolidColorBrush(Palette.Background);
        dc.DrawRectangle(bg, null, new Rect(size));
        var bounds = scene.WorldBounds();
        if (bounds is null)
        {
            worldToScreen = Matrix.Identity;
            DrawCentredText(dc, "Aucune carte", size);
            return;
        }
        var m = FitTransform(bounds.Value, size);
        if (zoom != 1 || pan != default)
        {
            m.ScaleAt(zoom, zoom, size.Width / 2, size.Height / 2);
            m.Translate(pan.X, pan.Y);
        }
        worldToScreen = m;

        if (scene.Grid is { } grid)
            DrawGrid(dc, grid, m, scene.SelectedZoneIds);
        else
            DrawVisitedPoints(dc, scene, m);

        var furniturePen = new Pen(new SolidColorBrush(Palette.Furniture), 1);
        var furnitureFill = new SolidColorBrush(Color.FromArgb(0x30, Palette.Furniture.R, Palette.Furniture.G, Palette.Furniture.B));
        foreach (var f in scene.Map?.Furniture ?? [])
            DrawPolygon(dc, f.Points, m, furniturePen, furnitureFill);

        foreach (var r in scene.Map?.Restrictions ?? [])
            DrawPolygon(dc, r.Points, m, new Pen(new SolidColorBrush(Color.FromRgb(0xe0, 0x50, 0x50)), 2), new SolidColorBrush(Color.FromArgb(0x40, 0xe0, 0x50, 0x50)));

        if (scene.Path is { Count: > 1 } path)
            DrawActionPath(dc, scene, path, m);

        foreach (var o in scene.Obstacles ?? [])
        {
            var p = m.Transform(new Point(o.X, o.Y));
            DrawObstacleMarker(dc, p);
        }

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

        if (scene.Dock is { } dock)
        {
            var p = m.Transform(new Point(dock.X, dock.Y));
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0xff, 0xd7, 0x00)), new Pen(Brushes.Black, 1), new Rect(p.X - 6, p.Y - 6, 12, 12));
        }

        if (scene.Robot is { } robot)
        {
            var p = m.Transform(new Point(robot.X, robot.Y));
            dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0x3c, 0xb4, 0x3c)), new Pen(Brushes.White, 1.5), p, 8, 8);
            var tip = m.Transform(new Point(robot.X + 0.35 * Math.Cos(robot.Angle), robot.Y + 0.35 * Math.Sin(robot.Angle)));
            dc.DrawLine(new Pen(Brushes.White, 2), p, tip);
        }
    }

    public static void Render(DrawingContext dc, MapScene scene, Size size, out Matrix worldToScreen) =>
        Render(dc, scene, size, out worldToScreen, 1, default);

    /// <summary>
    /// Draws the driven path in consecutive same-colour runs: grey where the robot was only
    /// repositioning (point's "update" is 0 or missing, e.g. path data with no such field), and
    /// otherwise the colour of the clean type assigned to whichever room the point falls in — the
    /// data only confirms a binary "working or not" flag per point, not which tool was engaged, so
    /// the tool colour is inferred from the room's own setting rather than observed directly.
    /// </summary>
    private static void DrawActionPath(DrawingContext dc, MapScene scene, IReadOnlyList<CorePoint> path, Matrix m)
    {
        var runStart = 0;
        var runColor = ActionColorAt(scene, path[0]);
        for (var i = 1; i < path.Count; i++)
        {
            var color = ActionColorAt(scene, path[i]);
            if (color == runColor) continue;
            DrawPathRun(dc, path, runStart, i, runColor, m);
            runStart = i;
            runColor = color;
        }
        DrawPathRun(dc, path, runStart, path.Count - 1, runColor, m);
    }

    /// <summary>Draws points [from, to] (inclusive) as one polyline; a single point has nothing to join, so it's skipped.</summary>
    private static void DrawPathRun(DrawingContext dc, IReadOnlyList<CorePoint> path, int from, int to, Color color, Matrix m)
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
        dc.DrawGeometry(null, new Pen(new SolidColorBrush(color), 2) { LineJoin = PenLineJoin.Round }, geo);
    }

    private static Color ActionColorAt(MapScene scene, CorePoint p)
    {
        if (p.Update is not 1) return Palette.Path;
        var zoneId = scene.ZoneAt(p.X, p.Y);
        var settings = zoneId is null ? null : scene.ZoneMetadata?.FirstOrDefault(z => z.Id == zoneId)?.Settings;
        var type = CleanTypes.FromRest(settings?.CleanType);
        return Palette.ActionColors[(int)type % Palette.ActionColors.Length];
    }

    private static void DrawObstacleMarker(DrawingContext dc, Point p)
    {
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.BeginFigure(new Point(p.X, p.Y - 7), true, true);
            g.LineTo(new Point(p.X + 6, p.Y + 5), true, true);
            g.LineTo(new Point(p.X - 6, p.Y + 5), true, true);
        }
        geo.Freeze();
        dc.DrawGeometry(new SolidColorBrush(Color.FromRgb(0xe0, 0xa0, 0x30)), new Pen(Brushes.Black, 1), geo);
        dc.DrawEllipse(Brushes.Black, null, new Point(p.X, p.Y + 1.5), 0.8, 0.8);
    }

    private static void DrawGrid(DrawingContext dc, MapGrid grid, Matrix m, IReadOnlySet<string>? selected)
    {
        var bmp = new WriteableBitmap(grid.Width, grid.Height, 96, 96, PixelFormats.Bgra32, null);
        var pixels = new int[grid.Width * grid.Height];
        for (var cy = 0; cy < grid.Height; cy++)
        {
            var row = (grid.Height - 1 - cy) * grid.Width;   // bitmap row 0 is the top, grid row 0 the bottom
            for (var cx = 0; cx < grid.Width; cx++)
            {
                var v = grid[cx, cy];
                Color c;
                if (v == MapGrid.Unknown) c = Colors.Transparent;
                else if (v == MapGrid.Obstacle) c = Palette.Obstacle;
                else
                {
                    c = ZoneColor(v);
                    if (selected is { Count: > 0 } && !selected.Contains(v.ToString(CultureInfo.InvariantCulture)))
                        c = Dim(c);
                }
                pixels[row + cx] = (c.A << 24) | (c.R << 16) | (c.G << 8) | c.B;
            }
        }
        bmp.WritePixels(new Int32Rect(0, 0, grid.Width, grid.Height), pixels, grid.Width * 4, 0);
        RenderOptions.SetBitmapScalingMode(bmp, BitmapScalingMode.NearestNeighbor); // before Freeze: a frozen bitmap is read-only
        bmp.Freeze();

        var worldRect = new Rect(new Point(grid.OffsetX, grid.OffsetY),
                                 new Point(grid.OffsetX + grid.Width * grid.Resolution, grid.OffsetY + grid.Height * grid.Resolution));
        var p0 = m.Transform(new Point(worldRect.Left, worldRect.Bottom));
        var p1 = m.Transform(new Point(worldRect.Right, worldRect.Top));
        dc.DrawImage(bmp, new Rect(p0, p1));
    }

    /// <summary>Without a grid (maps other than the current one), the visited points give the rooms' shape.</summary>
    private static void DrawVisitedPoints(DrawingContext dc, MapScene scene, Matrix m)
    {
        foreach (var z in scene.Map?.Zones ?? [])
        {
            if (z.Visited is not { Count: > 0 } pts || !int.TryParse(z.Id, out var id)) continue;
            var c = ZoneColor(id);
            if (scene.SelectedZoneIds is { Count: > 0 } sel && !sel.Contains(z.Id)) c = Dim(c);
            var brush = new SolidColorBrush(c);
            var r = Math.Max(2, 0.12 * m.M11);   // cell-sized dots
            foreach (var p in pts)
                dc.DrawEllipse(brush, null, m.Transform(new Point(p.X, p.Y)), r, r);
        }
    }

    private static Color Dim(Color c) =>
        Color.FromArgb(0xff, (byte)((c.R + Palette.Background.R * 2) / 3), (byte)((c.G + Palette.Background.G * 2) / 3), (byte)((c.B + Palette.Background.B * 2) / 3));

    private static void DrawPolygon(DrawingContext dc, IReadOnlyList<CorePoint>? points, Matrix m, Pen pen, Brush fill)
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
            new Typeface("Segoe UI"), 12, new SolidColorBrush(Palette.LabelText), 1.0);
        var rect = new Rect(at.X - ft.Width / 2 - 4, at.Y - ft.Height / 2 - 2, ft.Width + 8, ft.Height + 4);
        dc.DrawRoundedRectangle(new SolidColorBrush(Palette.LabelBackground), null, rect, 3, 3);
        dc.DrawText(ft, new Point(rect.X + 4, rect.Y + 2));
        return rect;
    }

    private static void DrawBadge(DrawingContext dc, string text, Point at)
    {
        var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal), 11, Brushes.Black, 1.0);
        dc.DrawEllipse(Brushes.White, new Pen(Brushes.Black, 1), at, 9, 9);
        dc.DrawText(ft, new Point(at.X - ft.Width / 2, at.Y - ft.Height / 2));
    }

    private static void DrawCentredText(DrawingContext dc, string text, Size size)
    {
        var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), 16, new SolidColorBrush(Palette.LabelText), 1.0);
        dc.DrawText(ft, new Point((size.Width - ft.Width) / 2, (size.Height - ft.Height) / 2));
    }

    /// <summary>Renders the scene to a PNG file, for sharing or for checking the renderer without a window.</summary>
    public static void ExportPng(MapScene scene, int width, int height, string path)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
            Render(dc, scene, new Size(width, height), out _);
        var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(path);
        encoder.Save(fs);
    }
}
