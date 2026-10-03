using System.Globalization;
using Dyss.Core;
using CorePoint = Dyss.Core.Point;

namespace Dyss.Presentation.Map;

/// <summary>What the robot icon shows it doing; see <see cref="RobotMarkerLayout"/>.</summary>
public enum RobotActivity
{
    Idle,
    /// <summary>The side brushes turn.</summary>
    Vacuuming,
    /// <summary>The roller's treads run backwards.</summary>
    Mopping,
    VacuumingAndMopping,
    /// <summary>Driving without cleaning: to a room, back to the dock, finding itself. Drawn still: its moving on the map says enough.</summary>
    Moving,
}

/// <summary>What the dock icon shows it doing; see <see cref="RobotMarkerLayout"/>.</summary>
public enum DockActivity
{
    Idle,
    Charging,
    EmptyingBin,
    /// <summary>Before a clean with mopping: the robot takes on clean water.</summary>
    FillingWater,
    /// <summary>The roller is washed: dirty water up into its tank, clean water down.</summary>
    WashingRoller,
    DryingRoller,
}

/// <summary>Points [From, To] of a path drawn in one colour: null Action where the robot was only repositioning, else the room's clean type.</summary>
public readonly record struct PathRun(int From, int To, CleanType? Action);

/// <summary>
/// Everything a map view knows how to draw. All optional; missing layers are skipped. World
/// coordinates are metres relative to the dock with y pointing up.
/// </summary>
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

    /// <summary>What the robot is doing, for its animation; see <see cref="RobotMarkerLayout"/>.</summary>
    public RobotActivity RobotActivity { get; init; }
    /// <summary>What the dock is doing, for its animation.</summary>
    public DockActivity DockActivity { get; init; }
    /// <summary>The robot sits on its dock: drawn on the plate, under the tanks, rather than where it reports itself.</summary>
    public bool RobotDocked { get; init; }
    /// <summary>Glide the robot between reported positions; see <see cref="Services.DisplaySettings.SmoothRobotMotion"/>.</summary>
    public bool SmoothRobotMotion { get; init; }

    /// <summary>
    /// Whether a dock location is a real one. Maps the robot has never cleaned on report a sentinel
    /// far off the floor plan, observed at (1100, 1100), which is nothing to draw.
    /// </summary>
    public static bool IsRealDock(DockLocation dock) => Math.Abs(dock.X) < 1000 && Math.Abs(dock.Y) < 1000;

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
    public Rect2? WorldBounds()
    {
        if (Grid is { } g && g.KnownBounds() is { } b)
        {
            var (x0, y0) = g.ToWorld(b.MinCx, b.MinCy);
            var (x1, y1) = g.ToWorld(b.MaxCx, b.MaxCy);
            return Rect2.FromCorners(new Vec2(x0 - g.Resolution, y0 - g.Resolution), new Vec2(x1 + g.Resolution, y1 + g.Resolution));
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
        return Rect2.FromCorners(new Vec2(minX - 0.5, minY - 0.5), new Vec2(maxX + 0.5, maxY + 0.5));
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
