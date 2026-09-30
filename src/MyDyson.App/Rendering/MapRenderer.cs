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

/// <summary>Points [From, To] of a path drawn in one colour: null Action where the robot was only repositioning, else the room's clean type.</summary>
public readonly record struct PathRun(int From, int To, CleanType? Action);

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
    public IReadOnlyList<DirtSpot>? DirtSpots { get; init; }
    public IReadOnlySet<string>? SelectedZoneIds { get; init; }
    /// <summary>Zone id to its position in the clean order, shown as a badge.</summary>
    public IReadOnlyDictionary<string, int>? ZoneOrder { get; init; }

    /// <summary>Draw the furniture outlines. See <see cref="Services.DisplaySettings"/>.</summary>
    public bool ShowFurniture { get; init; } = true;
    /// <summary>Draw the stretches where the robot was only repositioning, not working.</summary>
    public bool ShowTravelPath { get; init; } = true;

    /// <summary>The restriction zone (REST id) the map manager is acting on, outlined.</summary>
    public string? SelectedRestrictionId { get; init; }
    /// <summary>The piece of furniture (REST id) the map manager is acting on, outlined.</summary>
    public string? SelectedFurnitureId { get; init; }

    /// <summary>The zone drawn for a zone clean, not yet started: outlined over the map.</summary>
    public IReadOnlyList<CorePoint>? SpotZone { get; init; }

    /// <summary>How far the map is turned clockwise on screen: the stored map's orientation, 0 when it has none or an odd one.</summary>
    public int Orientation => Map?.Orientation is 90 or 180 or 270 ? Map.Orientation.Value : 0;

    /// <summary>
    /// The driven path cut into same-action runs, which is what the renderer colours by. Finding
    /// the room under each point is the expensive part (on maps without a grid it is a nearest-
    /// visited-point search), and the scene is immutable, so it is done once here rather than on
    /// every pan or zoom frame. Consecutive runs share their boundary point so the line stays joined.
    /// </summary>
    public IReadOnlyList<PathRun> PathRuns => _pathRuns ??= ComputePathRuns();
    private IReadOnlyList<PathRun>? _pathRuns;

    private List<PathRun> ComputePathRuns()
    {
        if (Path is not { Count: > 1 } path) return [];
        var cleanTypeByZone = new Dictionary<string, CleanType>(StringComparer.Ordinal);
        foreach (var z in ZoneMetadata ?? [])
            cleanTypeByZone[z.Id] = CleanTypes.FromRest(z.Settings?.CleanType);

        var runs = new List<PathRun>();
        var start = 0;
        var action = ActionAt(path[0]);
        for (var i = 1; i < path.Count; i++)
        {
            var a = ActionAt(path[i]);
            if (a == action) continue;
            runs.Add(new PathRun(start, i, action));
            start = i;
            action = a;
        }
        runs.Add(new PathRun(start, path.Count - 1, action));
        return runs;

        // The data only confirms a binary "working or not" flag per point, not which tool was
        // engaged, so the tool is inferred from the room's own setting rather than observed.
        CleanType? ActionAt(CorePoint p)
        {
            if (p.Update is not 1) return null;
            var zoneId = ZoneAt(p.X, p.Y);
            return zoneId is not null && cleanTypeByZone.TryGetValue(zoneId, out var t) ? t : CleanType.Vacuum;
        }
    }

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
        if (DirtSpots is { } dirt) pts.AddRange(dirt.Select(d => new CorePoint(d.X, d.Y)));
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

    /// <summary>
    /// The robot's cell grid, origin and step in metres: from the occupancy grid of the active map,
    /// else from the stored map's dimensions, which every map has. 5 cm on every map seen so far.
    /// </summary>
    public (double X0, double Y0, double Step)? GridSpec =>
        Grid is { Resolution: > 0 } g ? (g.OffsetX, g.OffsetY, g.Resolution)
        : Map?.Dimensions is { Resolution: > 0 } d ? (d.OffsetX, d.OffsetY, d.Resolution)
        : null;

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
        private readonly Dictionary<(int Id, bool Dimmed), Brush> _zoneBrushes = [];

        /// <summary>
        /// One colour per kind of restriction zone, so the four read apart at a glance: red where the
        /// robot must not go, amber where it climbs, blue where it only mops, violet where it only
        /// vacuums. Kept the same in both themes; the legend in the map manager uses them too.
        /// </summary>
        public static readonly Dictionary<string, (Pen Pen, Brush Fill)> Restrictions = new()
        {
            ["keepOut"] = RestrictionStyle(RestrictionColor("keepOut")),
            ["climbObstacle"] = RestrictionStyle(RestrictionColor("climbObstacle")),
            ["brushBarOff"] = RestrictionStyle(RestrictionColor("brushBarOff")),
            ["noMop"] = RestrictionStyle(RestrictionColor("noMop")),
        };
        public static readonly (Pen Pen, Brush Fill) UnknownRestriction = RestrictionStyle(Color.FromRgb(0x90, 0x90, 0x90));
        public static readonly Pen HandlePen = Frozen(new Pen(Brushes.Black, 1.5));
        public static readonly Brush SpotFill = Frozen(new SolidColorBrush(Color.FromArgb(0x40, 0xff, 0xd7, 0x00)));
        /// <summary>What the map manager has chosen: a thick outline over whatever it is.</summary>
        public static readonly Pen SelectionPen = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromRgb(0xff, 0xd7, 0x00))), 3) { LineJoin = PenLineJoin.Round });

        private static (Pen, Brush) RestrictionStyle(Color c) =>
            (Frozen(new Pen(Frozen(new SolidColorBrush(c)), 2)), Frozen(new SolidColorBrush(Color.FromArgb(0x50, c.R, c.G, c.B))));
        public static readonly Brush DockFill = Frozen(new SolidColorBrush(Color.FromRgb(0xff, 0xd7, 0x00)));
        public static readonly Brush RobotFill = Frozen(new SolidColorBrush(Color.FromRgb(0x3c, 0xb4, 0x3c)));
        public static readonly Brush ObstacleFill = Frozen(new SolidColorBrush(Color.FromRgb(0xe0, 0xa0, 0x30)));
        public static readonly Pen BlackPen = Frozen(new Pen(Brushes.Black, 1));
        public static readonly Pen RobotOutline = Frozen(new Pen(Brushes.White, 1.5));
        public static readonly Pen RobotHeading = Frozen(new Pen(Brushes.White, 2));
        /// <summary>The cut being aimed while splitting a room: dashed so it reads as a proposal, not as map data.</summary>
        public static readonly Pen CutPen = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromRgb(0xe0, 0x30, 0x30))), 2)
        {
            DashStyle = new DashStyle([4, 3], 0),
            LineJoin = PenLineJoin.Round,
        });
        public static readonly Typeface LabelFont = new("Segoe UI");
        public static readonly Typeface BadgeFont = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

        public Resources(MapPalette p)
        {
            Background = Frozen(new SolidColorBrush(p.Background));
            LabelText = Frozen(new SolidColorBrush(p.LabelText));
            LabelBackground = Frozen(new SolidColorBrush(p.LabelBackground));
            FurniturePen = Frozen(new Pen(Frozen(new SolidColorBrush(p.Furniture)), 1));
            FurnitureFill = Frozen(new SolidColorBrush(Color.FromArgb(0x30, p.Furniture.R, p.Furniture.G, p.Furniture.B)));
            GridPen = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromArgb(0x38, p.LabelText.R, p.LabelText.G, p.LabelText.B))), 1));
            PathPen = LinePen(p.Path);
            ActionPens = [.. p.ActionColors.Select(LinePen)];
        }

        public Brush ZoneBrush(int zoneId, bool dimmed)
        {
            if (!_zoneBrushes.TryGetValue((zoneId, dimmed), out var brush))
            {
                var c = ZoneColor(zoneId);
                _zoneBrushes[(zoneId, dimmed)] = brush = Frozen(new SolidColorBrush(dimmed ? Dim(c) : c));
            }
            return brush;
        }

        private static Pen LinePen(Color c) => Frozen(new Pen(Frozen(new SolidColorBrush(c)), 2) { LineJoin = PenLineJoin.Round });

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

    /// <summary>
    /// Uniform world-to-screen transform that fits the bounds into the size with a margin, the map
    /// turned <paramref name="orientation"/> degrees clockwise on screen — the map's own
    /// "orientation", set by the phone's rotate button or the map manager.
    /// </summary>
    public static Matrix FitTransform(Rect world, Size size, double margin = 16, int orientation = 0)
    {
        // y up in the world, down on screen; then the turn, which with y down is clockwise.
        var turn = Matrix.Identity;
        turn.Scale(1, -1);
        turn.Rotate(orientation);
        var corners = new[] { world.TopLeft, world.TopRight, world.BottomLeft, world.BottomRight }.Select(turn.Transform).ToList();
        var (minX, maxX, minY, maxY) = (corners.Min(p => p.X), corners.Max(p => p.X), corners.Min(p => p.Y), corners.Max(p => p.Y));

        var sx = (size.Width - 2 * margin) / (maxX - minX);
        var sy = (size.Height - 2 * margin) / (maxY - minY);
        var s = Math.Max(1e-6, Math.Min(sx, sy));
        var ox = margin + (size.Width - 2 * margin - (maxX - minX) * s) / 2;
        var oy = margin + (size.Height - 2 * margin - (maxY - minY) * s) / 2;
        var m = turn;
        m.Translate(-minX, -minY);
        m.Scale(s, s);
        m.Translate(ox, oy);
        return m;
    }

    /// <summary>Renders with an extra zoom about the viewport centre and a pan, both in screen pixels.</summary>
    public static void Render(DrawingContext dc, MapScene scene, Size size, out Matrix worldToScreen, double zoom = 1, Vector pan = default)
    {
        var res = Current;
        dc.DrawRectangle(res.Background, null, new Rect(size));
        var bounds = scene.WorldBounds();
        if (bounds is null)
        {
            worldToScreen = Matrix.Identity;
            DrawCentredText(dc, "Aucune carte", size);
            return;
        }
        var m = FitTransform(bounds.Value, size, orientation: scene.Orientation);
        if (zoom != 1 || pan != default)
        {
            m.ScaleAt(zoom, zoom, size.Width / 2, size.Height / 2);
            m.Translate(pan.X, pan.Y);
        }
        worldToScreen = m;

        if (scene.Grid is { } grid)
            DrawGrid(dc, grid, m, scene.SelectedZoneIds, scene.Orientation);
        else
            DrawVisitedPoints(dc, scene, m);

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

        foreach (var o in scene.Obstacles ?? [])
        {
            var p = m.Transform(new Point(o.X, o.Y));
            DrawObstacleMarker(dc, p);
        }

        foreach (var d in scene.DirtSpots ?? [])
        {
            var p = m.Transform(new Point(d.X, d.Y));
            DrawDirtMarker(dc, p);
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
            dc.DrawRectangle(Resources.DockFill, Resources.BlackPen, new Rect(p.X - 6, p.Y - 6, 12, 12));
        }

        if (scene.Robot is { } robot)
        {
            var p = m.Transform(new Point(robot.X, robot.Y));
            dc.DrawEllipse(Resources.RobotFill, Resources.RobotOutline, p, 8, 8);
            var tip = m.Transform(new Point(robot.X + 0.35 * Math.Cos(robot.Angle), robot.Y + 0.35 * Math.Sin(robot.Angle)));
            dc.DrawLine(Resources.RobotHeading, p, tip);
        }
    }

    public static void Render(DrawingContext dc, MapScene scene, Size size, out Matrix worldToScreen) =>
        Render(dc, scene, size, out worldToScreen, 1, default);

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
        dc.DrawGeometry(Resources.ObstacleFill, Resources.BlackPen, geo);
        dc.DrawEllipse(Brushes.Black, null, new Point(p.X, p.Y + 1.5), 0.8, 0.8);
    }

    /// <summary>
    /// A plain green dot, matching the colour of the app's own splash icon for "liquid" (the only
    /// stain type confirmed so far; the app shows several distinct icons by type, but the others
    /// haven't been observed in a capture yet, so there's nothing to distinguish them by here).
    /// </summary>
    private static void DrawDirtMarker(DrawingContext dc, Point p) =>
        dc.DrawEllipse(Resources.RobotFill, Resources.BlackPen, p, 5, 5);

    /// <summary>
    /// The cut being aimed while splitting a room: the first end as a ring, and the line to the
    /// cursor once there is one. Drawn by <see cref="Controls.MapView"/> over the finished scene,
    /// since it belongs to an interaction rather than to the map.
    /// </summary>
    public static void DrawPendingCut(DrawingContext dc, Point from, Point? to)
    {
        if (to is { } end) dc.DrawLine(Resources.CutPen, from, end);
        dc.DrawEllipse(null, Resources.CutPen, from, 5, 5);
        if (to is { } e2) dc.DrawEllipse(null, Resources.CutPen, e2, 5, 5);
    }

    /// <summary>The zone being drawn: its first corner, then the upright rectangle to the cursor. Screen points.</summary>
    public static void DrawPendingRectangle(DrawingContext dc, Point from, Point? to)
    {
        dc.DrawEllipse(null, Resources.CutPen, from, 5, 5);
        if (to is { } end) dc.DrawRectangle(null, Resources.CutPen, new Rect(from, end));
    }

    /// <summary>Where a piece of furniture would land: its outline, already in screen points.</summary>
    public static void DrawPendingShape(DrawingContext dc, IReadOnlyList<Point> corners)
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

    /// <summary>
    /// The robot's cell grid over the visible part of the map, while something is being aimed. Left
    /// out while the cells are under 6 px apart — at the usual zoom a 5 cm cell is a pixel or two,
    /// and the lines would only grey the map over.
    /// </summary>
    public static void DrawGridLines(DrawingContext dc, Matrix m, (double X0, double Y0, double Step) grid, Size size)
    {
        // The scale is the length of a world unit on screen, whichever way the map is turned.
        var stepPx = grid.Step * Math.Sqrt(m.M11 * m.M11 + m.M12 * m.M12);
        if (stepPx < 6 || !m.HasInverse) return;
        var inverse = m;
        inverse.Invert();
        var seen = new[] { new Point(0, 0), new Point(size.Width, 0), new Point(0, size.Height), new Point(size.Width, size.Height) }.Select(inverse.Transform).ToList();
        var (minX, maxX, minY, maxY) = (seen.Min(p => p.X), seen.Max(p => p.X), seen.Min(p => p.Y), seen.Max(p => p.Y));

        // World lines, drawn through the transform, so they follow a turned map too.
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            for (var k = Math.Ceiling((minX - grid.X0) / grid.Step); k <= Math.Floor((maxX - grid.X0) / grid.Step); k++)
            {
                var x = grid.X0 + k * grid.Step;
                g.BeginFigure(m.Transform(new Point(x, minY)), false, false);
                g.LineTo(m.Transform(new Point(x, maxY)), true, false);
            }
            for (var k = Math.Ceiling((minY - grid.Y0) / grid.Step); k <= Math.Floor((maxY - grid.Y0) / grid.Step); k++)
            {
                var y = grid.Y0 + k * grid.Step;
                g.BeginFigure(m.Transform(new Point(minX, y)), false, false);
                g.LineTo(m.Transform(new Point(maxX, y)), true, false);
            }
        }
        geo.Freeze();
        dc.DrawGeometry(null, Current.GridPen, geo);
    }

    /// <summary>A small cross where the pointer lands on the grid.</summary>
    public static void DrawSnapMarker(DrawingContext dc, Point at)
    {
        dc.DrawLine(Resources.CutPen, new Point(at.X - 6, at.Y), new Point(at.X + 6, at.Y));
        dc.DrawLine(Resources.CutPen, new Point(at.X, at.Y - 6), new Point(at.X, at.Y + 6));
    }

    /// <summary>Square grips on the corners of a shape that can be resized, in screen points.</summary>
    public static void DrawHandles(DrawingContext dc, IReadOnlyList<Point> corners)
    {
        foreach (var c in corners)
            dc.DrawRectangle(Brushes.White, Resources.HandlePen, new Rect(c.X - 5, c.Y - 5, 10, 10));
    }

    /// <summary>The colour a kind of restriction zone is drawn in (REST behaviour), for the map and its legend.</summary>
    public static Color RestrictionColor(string? behavior) => behavior switch
    {
        "keepOut" => Color.FromRgb(0xe0, 0x50, 0x50),
        "climbObstacle" => Color.FromRgb(0xe8, 0xa0, 0x20),
        "brushBarOff" => Color.FromRgb(0x3c, 0x96, 0xe6),
        "noMop" => Color.FromRgb(0xa0, 0x6c, 0xe6),
        _ => Color.FromRgb(0x90, 0x90, 0x90),
    };

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
            && SameSelection(_cachedSelected, selected))
            return _cachedGridBitmap;

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

        _cachedGrid = grid;
        _cachedSelected = selected;
        _cachedPalette = Palette;
        _cachedGridBitmap = bmp;
        return bmp;
    }

    /// <summary>An empty set and a null set both mean "nothing dimmed", so they compare equal.</summary>
    private static bool SameSelection(IReadOnlySet<string>? a, IReadOnlySet<string>? b)
    {
        var aEmpty = a is not { Count: > 0 };
        var bEmpty = b is not { Count: > 0 };
        if (aEmpty || bEmpty) return aEmpty == bEmpty;
        return a!.SetEquals(b!);
    }

    private static void DrawGrid(DrawingContext dc, MapGrid grid, Matrix m, IReadOnlySet<string>? selected, int orientation = 0)
    {
        var bmp = BuildGridBitmap(grid, selected);

        var worldRect = new Rect(new Point(grid.OffsetX, grid.OffsetY),
                                 new Point(grid.OffsetX + grid.Width * grid.Resolution, grid.OffsetY + grid.Height * grid.Resolution));
        var c0 = m.Transform(new Point(worldRect.Left, worldRect.Bottom));
        var c1 = m.Transform(new Point(worldRect.Right, worldRect.Top));
        // A destination rect at a fractional pixel position gets its edges blended against the
        // background regardless of scaling mode; snapping to whole device pixels is what actually
        // keeps cell boundaries crisp instead of a faint blur.
        var onScreen = new Rect(new Point(Math.Round(c0.X), Math.Round(c0.Y)), new Point(Math.Round(c1.X), Math.Round(c1.Y)));
        // A turned map still covers an upright rectangle on screen, quarter turns being the only
        // ones there are: the image is drawn unturned, its sides swapped for 90 and 270, and turned
        // about the centre of that rectangle.
        var quarter = orientation is 90 or 270;
        var (w, h) = quarter ? (onScreen.Height, onScreen.Width) : (onScreen.Width, onScreen.Height);
        var centre = new Point(onScreen.X + onScreen.Width / 2, onScreen.Y + onScreen.Height / 2);
        var p0 = new Point(centre.X - w / 2, centre.Y - h / 2);
        var p1 = new Point(centre.X + w / 2, centre.Y + h / 2);

        // NearestNeighbor on the bitmap keeps each 5 cm cell a sharp block instead of a smooth
        // gradient, but WPF's compositor still anti-aliases the drawn image's own edges unless told
        // not to. EdgeMode is scoped to this DrawingGroup rather than set on the MapView itself, so
        // labels, the robot and the path stay anti-aliased as normal.
        var group = new DrawingGroup();
        RenderOptions.SetEdgeMode(group, EdgeMode.Aliased);
        RenderOptions.SetBitmapScalingMode(group, BitmapScalingMode.NearestNeighbor);
        using (var gdc = group.Open())
        {
            if (orientation != 0) gdc.PushTransform(new RotateTransform(orientation, centre.X, centre.Y));
            gdc.DrawImage(bmp, new Rect(p0, p1));
            if (orientation != 0) gdc.Pop();
        }
        group.Freeze();
        dc.DrawDrawing(group);
    }

    /// <summary>Without a grid (maps other than the current one), the visited points give the rooms' shape.</summary>
    private static void DrawVisitedPoints(DrawingContext dc, MapScene scene, Matrix m)
    {
        foreach (var z in scene.Map?.Zones ?? [])
        {
            if (z.Visited is not { Count: > 0 } pts || !int.TryParse(z.Id, out var id)) continue;
            var dimmed = scene.SelectedZoneIds is { Count: > 0 } sel && !sel.Contains(z.Id);
            var brush = Current.ZoneBrush(id, dimmed);
            var r = Math.Max(2, 0.12 * Math.Sqrt(m.M11 * m.M11 + m.M12 * m.M12));   // cell-sized dots, however the map is turned
            foreach (var p in pts)
                dc.DrawEllipse(brush, null, m.Transform(new Point(p.X, p.Y)), r, r);
        }
    }

    private static Color Dim(Color c) =>
        Color.FromArgb(0xff, (byte)((c.R + Palette.Background.R * 2) / 3), (byte)((c.G + Palette.Background.G * 2) / 3), (byte)((c.B + Palette.Background.B * 2) / 3));

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
            Render(dc, scene, new Size(width, height), out _);
        var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(path);
        encoder.Save(fs);
    }
}
