using DyssCockpit.Core;

namespace DyssCockpit.Presentation.Map;

/// <summary>Which drawing a <see cref="MarkerPlacement"/> is for.</summary>
public enum MarkerPart
{
    /// <summary>The plate the robot backs onto, under it when docked.</summary>
    DockPlate,
    /// <summary>The dock's tower and its three tanks, over the back of a docked robot.</summary>
    DockTower,
    Robot,
}

/// <summary>One drawing of a marker: <see cref="Frame"/> takes its icon units to the screen.</summary>
public readonly record struct MarkerPlacement(MarkerPart Part, MapTransform Frame, RobotActivity Robot = RobotActivity.Idle, DockActivity Dock = DockActivity.Idle);

/// <summary>
/// The robot and its dock as simplified top views, after the product photos: a black disc with its
/// clean-water tank in violet at the back and the two side brushes in front (blue left, red right),
/// and the dock's tower with its three tanks (dust bin, dirty water, clean water) behind the plate the
/// robot backs onto. Drawn at real scale on the map, but never smaller than a few pixels, and animated
/// by what each is doing. Shapes are laid out in icon units, y pointing to the back of the robot (or
/// to the front of the dock), then placed on the map by one transform, so the map's flip and rotation
/// come for free. This is the layout — where, how big, in what colours, moving how — that every
/// renderer shares; drawing it is the renderer's own business.
/// </summary>
public static class RobotMarkerLayout
{
    /// <summary>The robot's radius on the floor, in metres: 37.3 cm wide by 37 cm long, per Dyson's specifications.</summary>
    public const double RobotRadius = 0.186;
    /// <summary>How far in front of the dock's reported point the robot sits once docked, in metres (measured: 0.25).</summary>
    public const double DockedOffset = 0.25;
    /// <summary>The robot's radius in icon units.</summary>
    public const double Units = 16;
    /// <summary>The dock is laid out in centimetres, its origin on its reported point, +y towards the side the robot leaves by.</summary>
    private const double DockUnit = 0.01;
    /// <summary>The smallest robot on screen, in pixels, so it stays recognisable when zoomed out.</summary>
    private const double MinimumRadiusPixels = 11;

    /// <summary>Whether anything is moving, so the view knows to keep redrawing.</summary>
    public static bool IsAnimated(MapScene scene) =>
        (scene.Robot is not null && scene.RobotActivity is not (RobotActivity.Idle or RobotActivity.Moving) && !scene.RobotDocked)
        || (scene.Dock is not null && scene.DockActivity != DockActivity.Idle);

    /// <summary>
    /// What to draw, in drawing order: the dock (plate, docked robot, tower), then the robot. The
    /// robot stands at <paramref name="robotAt"/> when given (see <see cref="RobotGlide"/>), else
    /// where it reported itself.
    /// </summary>
    public static IReadOnlyList<MarkerPlacement> Place(MapScene scene, MapTransform worldToScreen, RobotPosition? robotAt = null)
    {
        var placements = new List<MarkerPlacement>(4);
        var dock = scene.Dock is { } d && MapScene.IsRealDock(d) ? d : null;
        // Real scale, or both enlarged alike when zoomed out too far to read them.
        var enlarge = Enlargement(worldToScreen);
        var unit = RobotRadius / Units * enlarge;
        if (dock is not null)
        {
            var forward = new Vec2(Math.Cos(dock.Angle), Math.Sin(dock.Angle));
            var dockFrame = Frame(new Vec2(dock.X, dock.Y), forward, DockUnit * enlarge, mirrored: true).Then(worldToScreen);
            placements.Add(new MarkerPlacement(MarkerPart.DockPlate, dockFrame));
            if (scene.Robot is not null && scene.RobotDocked)
            {
                var offset = DockedOffset * enlarge;
                var at = new Vec2(dock.X + forward.X * offset, dock.Y + forward.Y * offset);
                placements.Add(new MarkerPlacement(MarkerPart.Robot, Frame(at, forward, unit, mirrored: false).Then(worldToScreen), RobotActivity.Idle));
            }
            placements.Add(new MarkerPlacement(MarkerPart.DockTower, dockFrame, Dock: scene.DockActivity));
        }
        if ((robotAt ?? scene.Robot) is { } robot && !(scene.RobotDocked && dock is not null))
        {
            var heading = new Vec2(Math.Cos(robot.Angle), Math.Sin(robot.Angle));
            placements.Add(new MarkerPlacement(MarkerPart.Robot, Frame(new Vec2(robot.X, robot.Y), heading, unit, mirrored: false).Then(worldToScreen), scene.RobotActivity));
        }
        return placements;
    }

    /// <summary>1 at real scale; more when the robot would otherwise be too small to read.</summary>
    private static double Enlargement(MapTransform worldToScreen) =>
        Math.Max(1, MinimumRadiusPixels / Math.Max(worldToScreen.ScaleFactor, 1e-9) / RobotRadius);

    /// <summary>
    /// Icon units to world metres. The robot's icon has its front at -y; the dock's has the side
    /// the robot leaves by at +y, which turns it half round, hence <paramref name="mirrored"/>.
    /// x is to the robot's right, or to the dock's left as seen standing in front of it.
    /// </summary>
    private static MapTransform Frame(Vec2 centre, Vec2 forward, double unit, bool mirrored)
    {
        var right = new Vec2(forward.Y, -forward.X);   // y points up on the map
        var sign = mirrored ? -1 : 1;
        return new MapTransform(sign * unit * right.X, sign * unit * right.Y, -sign * unit * forward.X, -sign * unit * forward.Y, centre.X, centre.Y);
    }

    // ---- The robot, in icon units ------------------------------------------------------

    /// <summary>The body's radius.</summary>
    public const double BodyRadius = 16;
    /// <summary>Where the two side brushes turn, under the front of the body.</summary>
    public static readonly Vec2 LeftBrushHub = new(-11, -11), RightBrushHub = new(11, -11);
    /// <summary>Long enough for the arms to reach past the rim of the body they sit under.</summary>
    public const double BrushArm = 8;
    public static readonly Rect2 WaterTank = new(-9, 8.5, 18, 16);
    /// <summary>The dust bin and its filter.</summary>
    public static readonly Rect2 Lid = new(-9, -3, 18, 7);
    public static readonly Vec2 ButtonCentre = new(0, -7.5);
    public const double ButtonRadius = 1.6;
    public static readonly Rect2 Roller = new(-10, 7, 20, 6);
    /// <summary>The spacing of the roller's treads.</summary>
    public const double TreadPitch = 2.5;
    /// <summary>The green light the robot shines on the floor ahead while cleaning, as the phone app draws it; SVG path data.</summary>
    public const string LightConePath = "M-6,-14 L-13,-31 Q0,-35 13,-31 L6,-14 Z";
    /// <summary>The light fades from the robot (y = -15, fully lit) to its far end (y = -33).</summary>
    public const double LightNear = -15, LightFar = -33;

    // ---- The dock, in centimetres --------------------------------------------------------
    // From the dock's reported point, +y towards the side the robot leaves by. The station is 44 cm
    // wide, tower and plate alike, and 50.8 cm long (Dyson's specifications). The docked robot's
    // centre lies DockedOffset ahead of the point, its front 2 cm short of the plate's rounded edge,
    // which places the back of the station.

    private const double StationWidth = 44, StationLength = 50.8;
    private const double DockedRobotY = DockedOffset / DockUnit;
    private const double PlateFront = DockedRobotY + RobotRadius / DockUnit + 1.9;
    private const double StationBack = PlateFront - StationLength;
    // The three tanks in a row, the same gap between each other and between them and the inner
    // edge of the tower's rim on all four sides, which sets how deep the tower is; the back of the
    // docked robot slips under its front edge.
    public const double TankRadius = 6.3;
    private const double TankGap = 1.25, TowerRim = 1.2;
    private const double TowerHeight = 2 * TankRadius + 2 * TankGap + TowerRim;
    public const double TowerFront = StationBack + TowerHeight;
    public const double TankY = TowerFront - TowerRim / 2 - TankGap - TankRadius;
    private const double TankStep = 2 * TankRadius + TankGap;
    /// <summary>The dust bin, the dirty-water tank and the clean-water tank, left to right.</summary>
    public static readonly IReadOnlyList<double> TankX = [-TankStep, 0, TankStep];
    public static readonly Rect2 Tower = new(-StationWidth / 2, StationBack, StationWidth, TowerHeight);
    public const double BinFilterRadius = 3.6;
    public const double TankDotRadius = 1.4;
    // Water runs between the tanks and the docked robot: clean water slants from the right-hand
    // tank towards it, dirty water rises from it into the middle one.
    public static readonly Vec2 CleanTankOutlet = new(12.5, TankY + 6), RobotInlet = new(3, DockedRobotY - 5);
    public static readonly Vec2 RobotOutlet = new(0, DockedRobotY - 7), DirtyTankInlet = new(0, TankY + 6);
    /// <summary>As wide as the tower, from under it to its rounded front edge, 45.5 cm ahead of the dock point; SVG path data.</summary>
    public const string PlatePath = "M-22,0 H22 V35.5 A22,10 0 0 1 -22,35.5 Z";
    /// <summary>Over the docked robot, 25 cm ahead of the dock point.</summary>
    public const string BoltPath = "M-1,29 L2,25 H-1 L2,21";
    public const string SwirlPath = "M0,-3.6 A3.6,3.6 0 0 1 3.6,0 M0,3.6 A3.6,3.6 0 0 1 -3.6,0";
    public const string HeatWavePath = "M0,-6 Q2,-3 0,0 Q-2,3 0,6";

    // ---- Animation, t in seconds ----------------------------------------------------------

    /// <summary>The left brush's turn in degrees, clockwise, sweeping inwards; the right one turns the other way.</summary>
    public static double BrushTurn(double t) => t / 0.8 * 360;
    /// <summary>How far the roller's treads have run backwards.</summary>
    public static double TreadShift(double t) => t / 0.35 % 1 * TreadPitch;
    /// <summary>The swirl in the bin while it is emptied, in degrees.</summary>
    public static double SwirlTurn(double t) => t / 0.8 * 360;
    /// <summary>An opacity breathing between 0.25 and 1 over <paramref name="period"/> seconds: the charging bolt, a tank at work.</summary>
    public static double Pulse(double t, double period) => 0.625 + 0.375 * Math.Cos(t * 2 * Math.PI / period);
    /// <summary>How far a drop has slid, 0 to 1, starting <paramref name="delay"/> seconds late: one a second.</summary>
    public static double DropPhase(double t, double delay) => ((t - delay) % 1 + 1) % 1;
    /// <summary>How far a wave of warm air has gone down, 0 to 1, starting <paramref name="delay"/> seconds late: one every 1.4 s.</summary>
    public static double HeatPhase(double t, double delay) => ((t - delay) / 1.4 % 1 + 1) % 1;
    /// <summary>A moving thing's opacity along its way: fading in over the first 30 %, then out.</summary>
    public static double Fade(double phase) => phase < 0.3 ? phase / 0.3 : 1 - (phase - 0.3) / 0.7;
    /// <summary>How far down a wave of warm air goes from the tower's front, in centimetres.</summary>
    public const double HeatTravel = 12;

    /// <summary>The colours, the same in both themes: the product's own.</summary>
    public static class Colors
    {
        public static readonly ArgbColor Body = ArgbColor.FromArgb(0xFF2C2C2A);
        public static readonly ArgbColor Rim = ArgbColor.FromArgb(0xFF888780);
        public static readonly ArgbColor WaterTank = ArgbColor.FromArgb(0xFF7F77DD);
        public static readonly ArgbColor Lid = ArgbColor.FromArgb(0xFF444441);
        public static readonly ArgbColor Button = ArgbColor.FromArgb(0xFFB4B2A9);
        public static readonly ArgbColor LeftBrush = ArgbColor.FromArgb(0xFF378ADD);
        public static readonly ArgbColor RightBrush = ArgbColor.FromArgb(0xFFE24B4A);
        public static readonly ArgbColor Roller = ArgbColor.FromArgb(0xFF185FA5);
        public static readonly ArgbColor Tread = ArgbColor.FromArgb(0xFFB5D4F4);
        /// <summary>Bright at the robot, fading out ahead of it.</summary>
        public static readonly ArgbColor LightNear = ArgbColor.FromArgb(0xB04CE05A), LightFar = ArgbColor.FromArgb(0x004CE05A);
        public static readonly ArgbColor Plate = ArgbColor.FromArgb(0xFF444441);
        public static readonly ArgbColor PlateRim = ArgbColor.FromArgb(0xFF888780);
        public static readonly ArgbColor Bin = ArgbColor.FromArgb(0xFF534AB7);
        public static readonly ArgbColor BinFilter = ArgbColor.FromArgb(0xFFB4B2A9);
        public static readonly ArgbColor TankRim = ArgbColor.FromArgb(0xFF5F5E5A);
        public static readonly ArgbColor DirtyDot = ArgbColor.FromArgb(0xFF888780);
        public static readonly ArgbColor CleanDot = ArgbColor.FromArgb(0xFF378ADD);
        public static readonly ArgbColor DirtyGlow = ArgbColor.FromArgb(0xFF888780);
        public static readonly ArgbColor CleanGlow = ArgbColor.FromArgb(0xFF378ADD);
        public static readonly ArgbColor DirtyDrop = ArgbColor.FromArgb(0xFFBA7517);
        public static readonly ArgbColor CleanDrop = ArgbColor.FromArgb(0xFF85B7EB);
        public static readonly ArgbColor Bolt = ArgbColor.FromArgb(0xFF97C459);
        public static readonly ArgbColor Swirl = ArgbColor.FromArgb(0xFFEEEDFE);
        public static readonly ArgbColor Heat = ArgbColor.FromArgb(0xFFEF9F27);
    }
}
