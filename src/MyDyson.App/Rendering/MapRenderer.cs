using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MyDyson.Core;
using Point = System.Windows.Point;
using CorePoint = MyDyson.Core.Point;

namespace MyDyson.App.Rendering;

/// <summary>Everything the map view knows how to draw. All optional; missing layers are skipped.</summary>
public sealed class MapScene
{
    public MapGrid? Grid { get; init; }
    public PersistentMap? Map { get; init; }
    public IReadOnlyList<ZoneMetadata>? ZoneMetadata { get; init; }
    public RobotPosition? Robot { get; init; }
    public DockLocation? Dock { get; init; }
    public IReadOnlyList<CorePoint>? Path { get; init; }
    public IReadOnlySet<string>? SelectedZoneIds { get; init; }

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
        if (Dock is { } d) pts.Add(new CorePoint(d.X, d.Y));
        if (pts.Count == 0) return null;
        var minX = pts.Min(p => p.X); var maxX = pts.Max(p => p.X);
        var minY = pts.Min(p => p.Y); var maxY = pts.Max(p => p.Y);
        return new Rect(new Point(minX - 0.5, minY - 0.5), new Point(maxX + 0.5, maxY + 0.5));
    }
}

/// <summary>
/// Draws a <see cref="MapScene"/> onto a DrawingContext. World coordinates are metres relative to
/// the dock with y pointing up; the screen has y pointing down, so the transform flips it.
/// Shared by the on-screen control and the PNG export, so both agree.
/// </summary>
public static class MapRenderer
{
    private static readonly Color Background = Color.FromRgb(0x1e, 0x1e, 0x22);
    private static readonly Color ObstacleColor = Color.FromRgb(0x50, 0x50, 0x58);
    private static readonly Color[] ZonePalette =
    [
        Color.FromRgb(0x6c, 0x9e, 0xd6), Color.FromRgb(0x8b, 0xc3, 0x8b), Color.FromRgb(0xd6, 0xa7, 0x6c),
        Color.FromRgb(0xc3, 0x8b, 0xb8), Color.FromRgb(0x6c, 0xd6, 0xc9), Color.FromRgb(0xd6, 0xd0, 0x6c),
        Color.FromRgb(0xb8, 0x8b, 0x6c), Color.FromRgb(0x9e, 0x6c, 0xd6),
    ];

    public static Color ZoneColor(int zoneId) => ZonePalette[Math.Abs(zoneId) % ZonePalette.Length];

    /// <summary>Uniform world-to-screen transform that fits the bounds into the size with a margin.</summary>
    public static Matrix FitTransform(Rect world, Size size, double margin = 16)
    {
        var sx = (size.Width - 2 * margin) / world.Width;
        var sy = (size.Height - 2 * margin) / world.Height;
        var s = Math.Max(1e-6, Math.Min(sx, sy));
        // x right, y up: screen_x = (x - minX) * s + ox ; screen_y = (maxY - y) * s + oy
        var ox = margin + (size.Width - 2 * margin - world.Width * s) / 2;
        var oy = margin + (size.Height - 2 * margin - world.Height * s) / 2;
        var m = Matrix.Identity;
        m.Translate(-world.X, -world.Bottom);   // bring (minX, maxY) to origin; Bottom is maxY here since Rect y grows "up" in our use
        m.Scale(s, -s);
        m.Translate(ox, oy);
        return m;
    }

    public static void Render(DrawingContext dc, MapScene scene, Size size, out Matrix worldToScreen)
    {
        dc.DrawRectangle(new SolidColorBrush(Background), null, new Rect(size));
        var bounds = scene.WorldBounds();
        if (bounds is null)
        {
            worldToScreen = Matrix.Identity;
            DrawCentredText(dc, "Aucune carte", size);
            return;
        }
        var m = FitTransform(bounds.Value, size);
        worldToScreen = m;

        if (scene.Grid is { } grid)
            DrawGrid(dc, grid, m, scene.SelectedZoneIds);

        foreach (var f in scene.Map?.Furniture ?? [])
            DrawPolygon(dc, f.Points, m, new Pen(new SolidColorBrush(Color.FromArgb(0xa0, 0xff, 0xff, 0xff)), 1), new SolidColorBrush(Color.FromArgb(0x30, 0xff, 0xff, 0xff)));

        foreach (var r in scene.Map?.Restrictions ?? [])
            DrawPolygon(dc, r.Points, m, new Pen(new SolidColorBrush(Color.FromRgb(0xe0, 0x50, 0x50)), 2), new SolidColorBrush(Color.FromArgb(0x40, 0xe0, 0x50, 0x50)));

        if (scene.Path is { Count: > 1 } path)
        {
            var geo = new StreamGeometry();
            using (var g = geo.Open())
            {
                g.BeginFigure(m.Transform(new Point(path[0].X, path[0].Y)), false, false);
                for (var i = 1; i < path.Count; i++)
                    g.LineTo(m.Transform(new Point(path[i].X, path[i].Y)), true, false);
            }
            geo.Freeze();
            dc.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb(0xc0, 0xff, 0xff, 0xff)), 1.5) { LineJoin = PenLineJoin.Round }, geo);
        }

        foreach (var z in scene.Map?.Zones ?? [])
        {
            if (z.NameLocation is not { } n) continue;
            var label = scene.ZoneMetadata?.FirstOrDefault(zm => zm.Id == z.Id)?.Name ?? z.Name ?? z.Id;
            DrawLabel(dc, label, m.Transform(new Point(n.X, n.Y)));
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
            // Heading: angle in radians, 0 along +x, counter-clockwise in world space.
            var tip = m.Transform(new Point(robot.X + 0.35 * Math.Cos(robot.Angle), robot.Y + 0.35 * Math.Sin(robot.Angle)));
            dc.DrawLine(new Pen(Brushes.White, 2), p, tip);
        }
    }

    private static void DrawGrid(DrawingContext dc, MapGrid grid, Matrix m, IReadOnlySet<string>? selected)
    {
        // One bitmap pixel per cell, drawn through the transform. Cells are tiny at fit scale, so
        // rendering the bitmap scaled is far cheaper than one rectangle per cell.
        var bmp = new WriteableBitmap(grid.Width, grid.Height, 96, 96, PixelFormats.Bgra32, null);
        var pixels = new int[grid.Width * grid.Height];
        for (var cy = 0; cy < grid.Height; cy++)
        {
            // Bitmap row 0 is the top; grid row 0 is the bottom (y up), so flip here.
            var row = (grid.Height - 1 - cy) * grid.Width;
            for (var cx = 0; cx < grid.Width; cx++)
            {
                var v = grid[cx, cy];
                Color c;
                if (v == MapGrid.Unknown) c = Colors.Transparent;
                else if (v == MapGrid.Obstacle) c = ObstacleColor;
                else
                {
                    c = ZoneColor(v);
                    if (selected is { Count: > 0 } && !selected.Contains(v.ToString(CultureInfo.InvariantCulture)))
                        c = Color.FromArgb(0xff, (byte)(c.R / 2 + 0x20), (byte)(c.G / 2 + 0x20), (byte)(c.B / 2 + 0x20));
                }
                pixels[row + cx] = (c.A << 24) | (c.R << 16) | (c.G << 8) | c.B;
            }
        }
        bmp.WritePixels(new Int32Rect(0, 0, grid.Width, grid.Height), pixels, grid.Width * 4, 0);
        RenderOptions.SetBitmapScalingMode(bmp, BitmapScalingMode.NearestNeighbor); // before Freeze: a frozen bitmap is read-only
        bmp.Freeze();

        // Destination rectangle in screen space: the world rect covered by the grid.
        var worldRect = new Rect(new Point(grid.OffsetX, grid.OffsetY),
                                 new Point(grid.OffsetX + grid.Width * grid.Resolution, grid.OffsetY + grid.Height * grid.Resolution));
        var p0 = m.Transform(new Point(worldRect.Left, worldRect.Bottom));  // top-left on screen (max y)
        var p1 = m.Transform(new Point(worldRect.Right, worldRect.Top));   // bottom-right on screen (min y)
        dc.DrawImage(bmp, new Rect(p0, p1));
    }

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

    private static void DrawLabel(DrawingContext dc, string text, Point at)
    {
        var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), 12, Brushes.White, 1.0);
        var rect = new Rect(at.X - ft.Width / 2 - 4, at.Y - ft.Height / 2 - 2, ft.Width + 8, ft.Height + 4);
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(0xa0, 0, 0, 0)), null, rect, 3, 3);
        dc.DrawText(ft, new Point(rect.X + 4, rect.Y + 2));
    }

    private static void DrawCentredText(DrawingContext dc, string text, Size size)
    {
        var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), 16, Brushes.Gray, 1.0);
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
