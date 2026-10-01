using System.Windows;
using System.Windows.Media;
using MyDyson.Core;
using Point = System.Windows.Point;

namespace MyDyson.App.Rendering;

/// <summary>What the robot icon shows it doing; see <see cref="RobotMarkers"/>.</summary>
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

/// <summary>What the dock icon shows it doing; see <see cref="RobotMarkers"/>.</summary>
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

/// <summary>
/// The robot and its dock as simplified top views, after the product photos: a black disc with its
/// clean-water tank in violet at the back and the two side brushes in front (blue left, red right),
/// and the dock's tower with its three tanks (dust bin, dirty water, clean water) behind the plate the
/// robot backs onto. Drawn at real scale on the map, but never smaller than a few pixels, and animated
/// by what each is doing. Shapes are laid out in icon units, y pointing to the back of the robot (or
/// to the front of the dock), then placed on the map by one matrix, so the map's flip and rotation
/// come for free.
/// </summary>
public static class RobotMarkers
{
    /// <summary>The robot's radius on the floor, in metres.</summary>
    public const double RobotRadius = 0.175;
    /// <summary>How far in front of the dock's reported point the robot sits once docked, in metres (measured: 0.25).</summary>
    public const double DockedOffset = 0.25;
    /// <summary>The robot's radius in icon units.</summary>
    private const double Units = 16;
    /// <summary>The dock is laid out in units 1.25 times the robot's, as on the approved drawing.</summary>
    private const double DockScale = 1.25;
    /// <summary>Where the dock's reported point falls in the dock's own units: the robot's centre lies DockedOffset in front of it.</summary>
    private const double DockedRobotY = 9;
    /// <summary>The smallest robot on screen, in pixels, so it stays recognisable when zoomed out.</summary>
    private const double MinimumRadiusPixels = 11;

    /// <summary>Whether anything is moving, so the view knows to keep redrawing.</summary>
    public static bool IsAnimated(MapScene scene) =>
        (scene.Robot is not null && scene.RobotActivity is not (RobotActivity.Idle or RobotActivity.Moving) && !scene.RobotDocked)
        || (scene.Dock is not null && scene.DockActivity != DockActivity.Idle);

    /// <summary>
    /// Draws the dock, then the robot, at <paramref name="seconds"/> into their animations. The robot
    /// stands at <paramref name="robotAt"/> when given (see <see cref="RobotGlide"/>), else where it reported itself.
    /// </summary>
    public static void Draw(DrawingContext dc, MapScene scene, Matrix worldToScreen, double seconds, RobotPosition? robotAt = null)
    {
        var dock = scene.Dock is { } d && MapScene.IsRealDock(d) ? d : null;
        var unit = UnitLength(worldToScreen);
        if (dock is not null)
        {
            var forward = new Vector(Math.Cos(dock.Angle), Math.Sin(dock.Angle));
            var dockFrame = Frame(new Point(dock.X, dock.Y), forward, unit * DockScale, mirrored: true) * worldToScreen;
            // The dock's point in its own units is where the robot's centre lies DockedOffset metres behind.
            var origin = new Vector(0, DockedRobotY - DockedOffset / (unit * DockScale));
            dockFrame = Translated(-origin) * dockFrame;
            DrawPlate(dc, dockFrame);
            if (scene.Robot is not null && scene.RobotDocked)
            {
                var at = new Point(dock.X + forward.X * DockedOffset, dock.Y + forward.Y * DockedOffset);
                DrawRobot(dc, Frame(at, forward, unit, mirrored: false) * worldToScreen, RobotActivity.Idle, seconds);
            }
            DrawTower(dc, dockFrame, scene.DockActivity, seconds);
        }
        if ((robotAt ?? scene.Robot) is { } robot && !(scene.RobotDocked && dock is not null))
        {
            var heading = new Vector(Math.Cos(robot.Angle), Math.Sin(robot.Angle));
            DrawRobot(dc, Frame(new Point(robot.X, robot.Y), heading, unit, mirrored: false) * worldToScreen, scene.RobotActivity, seconds);
        }
    }

    /// <summary>Metres per icon unit: real scale, or larger when that would make the robot too small to read.</summary>
    private static double UnitLength(Matrix worldToScreen)
    {
        var pixelsPerMetre = new Vector(worldToScreen.M11, worldToScreen.M12).Length;
        var radius = Math.Max(RobotRadius, MinimumRadiusPixels / Math.Max(pixelsPerMetre, 1e-9));
        return radius / Units;
    }

    /// <summary>
    /// Icon units to world metres. The robot's icon has its front at -y; the dock's has the side
    /// the robot leaves by at +y, which turns it half round, hence <paramref name="mirrored"/>.
    /// x is to the robot's right, or to the dock's left as seen standing in front of it.
    /// </summary>
    private static Matrix Frame(Point centre, Vector forward, double unit, bool mirrored)
    {
        var right = new Vector(forward.Y, -forward.X);   // y points up on the map
        var sign = mirrored ? -1 : 1;
        return new Matrix(sign * unit * right.X, sign * unit * right.Y, -sign * unit * forward.X, -sign * unit * forward.Y, centre.X, centre.Y);
    }

    private static Matrix Translated(Vector by) => new(1, 0, 0, 1, by.X, by.Y);

    // ---- The robot ---------------------------------------------------------------

    private static void DrawRobot(DrawingContext dc, Matrix frame, RobotActivity activity, double t)
    {
        dc.PushTransform(new MatrixTransform(frame));
        // The green light it shines on the floor ahead while cleaning, as the phone app draws it.
        if (activity is RobotActivity.Vacuuming or RobotActivity.Mopping or RobotActivity.VacuumingAndMopping)
            dc.DrawGeometry(Palette.Light, null, LightCone);

        // The side brushes turn under the front of the body, only their tips showing past its rim.
        var spinning = activity is RobotActivity.Vacuuming or RobotActivity.VacuumingAndMopping;
        var turn = spinning ? t / 0.8 * 360 : 0;
        DrawBrush(dc, new Point(-11, -11), Palette.LeftBrush, turn);    // turns clockwise, sweeping inwards
        DrawBrush(dc, new Point(11, -11), Palette.RightBrush, -turn);   // its mirror image

        dc.DrawEllipse(Palette.Body, Palette.Rim, default, 16, 16);
        dc.PushClip(BodyClip);
        dc.DrawRoundedRectangle(Palette.WaterTank, null, new Rect(-9, 8.5, 18, 16), 2.5, 2.5);
        dc.Pop();
        dc.DrawRoundedRectangle(Palette.Lid, null, new Rect(-9, -3, 18, 7), 1.5, 1.5);   // the dust bin and its filter
        dc.DrawEllipse(Palette.Button, null, new Point(0, -7.5), 1.6, 1.6);

        if (activity is RobotActivity.Mopping or RobotActivity.VacuumingAndMopping) DrawRoller(dc, t);
        dc.Pop();
    }

    /// <summary>Long enough for the arms to reach past the rim of the body they sit under.</summary>
    private const double BrushArm = 8;

    private static void DrawBrush(DrawingContext dc, Point hub, Pen pen, double degrees)
    {
        dc.PushTransform(new RotateTransform(degrees, hub.X, hub.Y));
        for (var arm = 0; arm < 3; arm++)
        {
            var a = (arm * 120 - 90) * Math.PI / 180;
            dc.DrawLine(pen, hub, new Point(hub.X + BrushArm * Math.Cos(a), hub.Y + BrushArm * Math.Sin(a)));
        }
        dc.Pop();
    }

    /// <summary>The roller across the back, its treads running backwards like a tractor tyre's.</summary>
    private static void DrawRoller(DrawingContext dc, double t)
    {
        var roller = new Rect(-10, 7, 20, 6);
        dc.DrawRoundedRectangle(Palette.Roller, null, roller, 3, 3);
        dc.PushClip(new RectangleGeometry(roller, 3, 3));
        var shift = t / 0.35 % 1 * 2.5;
        for (var y = roller.Top - 5 + shift; y < roller.Bottom + 2.5; y += 2.5)
        {
            dc.DrawLine(Palette.Tread, new Point(-10, y), new Point(0, y + 2));
            dc.DrawLine(Palette.Tread, new Point(0, y + 2), new Point(10, y));
        }
        dc.Pop();
    }

    // ---- The dock ----------------------------------------------------------------

    private static readonly double[] TankX = [-14.15, 0, 14.15];
    private const double TankY = -15, TankRadius = 6.3;
    // Water runs between the tanks and the docked robot, whose centre is at (0, 9): clean water
    // slants from the right-hand tank towards it, dirty water rises from it into the middle one.
    private static readonly Point CleanTankOutlet = new(13, -9), RobotInlet = new(3, 4);
    private static readonly Point RobotOutlet = new(0, 2), DirtyTankInlet = new(0, -9);

    private static void DrawPlate(DrawingContext dc, Matrix frame)
    {
        dc.PushTransform(new MatrixTransform(frame));
        dc.DrawGeometry(Palette.Plate, Palette.PlateRim, Plate);
        dc.Pop();
    }

    private static void DrawTower(DrawingContext dc, Matrix frame, DockActivity activity, double t)
    {
        dc.PushTransform(new MatrixTransform(frame));
        dc.DrawRoundedRectangle(Palette.Body, Palette.Rim, new Rect(-22, -26, 44, 22), 4, 4);
        dc.DrawEllipse(Palette.Bin, null, new Point(TankX[0], TankY), TankRadius, TankRadius);
        dc.DrawEllipse(Palette.BinFilter, null, new Point(TankX[0], TankY), 3.6, 3.6);
        foreach (var x in TankX.Skip(1))
            dc.DrawEllipse(Palette.Lid, Palette.TankRim, new Point(x, TankY), TankRadius, TankRadius);
        dc.DrawEllipse(Palette.DirtyDot, null, new Point(TankX[1], TankY), 1.4, 1.4);
        dc.DrawEllipse(Palette.CleanDot, null, new Point(TankX[2], TankY), 1.4, 1.4);

        switch (activity)
        {
            case DockActivity.Charging:
                dc.PushOpacity(Pulse(t, 1.4));
                dc.DrawGeometry(null, Palette.Bolt, Bolt);
                dc.Pop();
                break;
            case DockActivity.EmptyingBin:
                dc.PushTransform(new RotateTransform(t / 0.8 * 360, TankX[0], TankY));
                dc.DrawGeometry(null, Palette.Swirl, Swirl);
                dc.Pop();
                break;
            case DockActivity.FillingWater:
                DrawTankGlow(dc, TankX[2], Palette.CleanGlow, t);
                DrawDrop(dc, Palette.CleanDrop, CleanTankOutlet, RobotInlet, t, 0);
                DrawDrop(dc, Palette.CleanDrop, CleanTankOutlet, RobotInlet, t, 0.5);
                break;
            case DockActivity.WashingRoller:
                DrawTankGlow(dc, TankX[1], Palette.DirtyGlow, t);
                DrawTankGlow(dc, TankX[2], Palette.CleanGlow, t + 0.5);
                DrawDrop(dc, Palette.DirtyDrop, RobotOutlet, DirtyTankInlet, t, 0);
                DrawDrop(dc, Palette.DirtyDrop, RobotOutlet, DirtyTankInlet, t, 0.5);
                DrawDrop(dc, Palette.CleanDrop, CleanTankOutlet, RobotInlet, t, 0.25);
                break;
            case DockActivity.DryingRoller:
                for (var i = 0; i < 3; i++) DrawHeatWave(dc, -6 + 6 * i, t, i * 0.45);
                break;
        }
        dc.Pop();
    }

    private static double Pulse(double t, double period) => 0.625 + 0.375 * Math.Cos(t * 2 * Math.PI / period);

    private static void DrawTankGlow(DrawingContext dc, double x, Brush brush, double t)
    {
        dc.PushOpacity(Pulse(t, 1));
        dc.DrawEllipse(brush, null, new Point(x, TankY), 3, 3);
        dc.Pop();
    }

    /// <summary>A drop sliding from <paramref name="from"/> to <paramref name="to"/> each second, fading in then out.</summary>
    private static void DrawDrop(DrawingContext dc, Brush brush, Point from, Point to, double t, double delay)
    {
        var phase = ((t - delay) % 1 + 1) % 1;
        dc.PushOpacity(Fade(phase));
        dc.DrawEllipse(brush, null, from + (to - from) * phase, 1.4, 1.4);
        dc.Pop();
    }

    /// <summary>Warm air going down from the tower to the robot on the plate.</summary>
    private static void DrawHeatWave(DrawingContext dc, double x, double t, double delay)
    {
        var phase = ((t - delay) / 1.4 % 1 + 1) % 1;
        dc.PushOpacity(Fade(phase));
        dc.PushTransform(new TranslateTransform(x, -3 + phase * 12));
        dc.DrawGeometry(null, Palette.Heat, HeatWave);
        dc.Pop();
        dc.Pop();
    }

    private static double Fade(double phase) => phase < 0.3 ? phase / 0.3 : 1 - (phase - 0.3) / 0.7;

    // ---- Shapes and colours ------------------------------------------------------------

    private static readonly Geometry BodyClip = Frozen(new EllipseGeometry(default, 16, 16));
    private static readonly Geometry LightCone = Frozen(Geometry.Parse("M-6,-14 L-13,-31 Q0,-35 13,-31 L6,-14 Z"));
    private static readonly Geometry Plate = Frozen(Geometry.Parse("M-19,-6 H19 V14 A19,10 0 0 1 -19,14 Z"));
    private static readonly Geometry Bolt = Frozen(Geometry.Parse("M-1,15 L2,11 H-1 L2,7"));
    private static readonly Geometry Swirl = Frozen(Geometry.Parse("M-14.15,-18.6 A3.6,3.6 0 0 1 -10.55,-15 M-14.15,-11.4 A3.6,3.6 0 0 1 -17.75,-15"));
    private static readonly Geometry HeatWave = Frozen(Geometry.Parse("M0,-6 Q2,-3 0,0 Q-2,3 0,6"));

    private static T Frozen<T>(T f) where T : Freezable { f.Freeze(); return f; }
    private static SolidColorBrush Solid(uint argb) => Frozen(new SolidColorBrush(Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb)));
    private static Pen Line(uint argb, double width) => Frozen(new Pen(Solid(argb), width) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round });

    private static class Palette
    {
        public static readonly Brush Body = Solid(0xFF2C2C2A);
        public static readonly Pen Rim = Line(0xFF888780, 1.2);
        public static readonly Brush WaterTank = Solid(0xFF7F77DD);
        public static readonly Brush Lid = Solid(0xFF444441);
        public static readonly Brush Button = Solid(0xFFB4B2A9);
        public static readonly Pen LeftBrush = Line(0xFF378ADD, 1.8);
        public static readonly Pen RightBrush = Line(0xFFE24B4A, 1.8);
        public static readonly Brush Roller = Solid(0xFF185FA5);
        public static readonly Pen Tread = Line(0xFFB5D4F4, 0.9);
        /// <summary>Bright at the robot, fading out ahead of it.</summary>
        public static readonly Brush Light = Frozen(new LinearGradientBrush(
            new GradientStopCollection { new(Color.FromArgb(0xB0, 0x4C, 0xE0, 0x5A), 0), new(Color.FromArgb(0x00, 0x4C, 0xE0, 0x5A), 1) },
            new Point(0, 0), new Point(0, 1)) { MappingMode = BrushMappingMode.Absolute, StartPoint = new Point(0, -15), EndPoint = new Point(0, -33) });
        public static readonly Brush Plate = Solid(0xFF444441);
        public static readonly Pen PlateRim = Line(0xFF888780, 1);
        public static readonly Brush Bin = Solid(0xFF534AB7);
        public static readonly Brush BinFilter = Solid(0xFFB4B2A9);
        public static readonly Pen TankRim = Line(0xFF5F5E5A, 1);
        public static readonly Brush DirtyDot = Solid(0xFF888780);
        public static readonly Brush CleanDot = Solid(0xFF378ADD);
        public static readonly Brush DirtyGlow = Solid(0xFF888780);
        public static readonly Brush CleanGlow = Solid(0xFF378ADD);
        public static readonly Brush DirtyDrop = Solid(0xFFBA7517);
        public static readonly Brush CleanDrop = Solid(0xFF85B7EB);
        public static readonly Pen Bolt = Line(0xFF97C459, 1.6);
        public static readonly Pen Swirl = Line(0xFFEEEDFE, 1.4);
        public static readonly Pen Heat = Line(0xFFEF9F27, 1.4);
    }
}
