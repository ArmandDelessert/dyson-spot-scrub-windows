using System.Windows;
using System.Windows.Media;
using Dyss.Core;
using Dyss.Presentation.Map;
using static Dyss.Presentation.Map.RobotMarkerLayout;
using MarkerColors = Dyss.Presentation.Map.RobotMarkerLayout.Colors;
using Point = System.Windows.Point;

namespace Dyss.App.Rendering;

/// <summary>
/// Draws the robot and its dock as <see cref="RobotMarkerLayout"/> lays them out: where, how big, in
/// what colours and moving how is decided there; this turns it into WPF drawing calls.
/// </summary>
public static class RobotMarkers
{
    /// <summary>
    /// Draws the dock, then the robot, at <paramref name="seconds"/> into their animations. The robot
    /// stands at <paramref name="robotAt"/> when given (see <see cref="RobotGlide"/>), else where it reported itself.
    /// </summary>
    public static void Draw(DrawingContext dc, MapScene scene, MapTransform worldToScreen, double seconds, RobotPosition? robotAt = null)
    {
        foreach (var marker in Place(scene, worldToScreen, robotAt))
        {
            var frame = marker.Frame.ToWpf();
            switch (marker.Part)
            {
                case MarkerPart.DockPlate: DrawPlate(dc, frame); break;
                case MarkerPart.DockTower: DrawTower(dc, frame, marker.Dock, seconds); break;
                case MarkerPart.Robot: DrawRobot(dc, frame, marker.Robot, seconds); break;
            }
        }
    }

    /// <summary>The robot as the application's icon: cleaning, its light on, brushes at rest. See <see cref="AppIcon"/>.</summary>
    public static void DrawIcon(DrawingContext dc, Matrix frame) => DrawRobot(dc, frame, RobotActivity.Vacuuming, 0);

    // ---- The robot ---------------------------------------------------------------

    private static void DrawRobot(DrawingContext dc, Matrix frame, RobotActivity activity, double t)
    {
        dc.PushTransform(new MatrixTransform(frame));
        // The green light it shines on the floor ahead while cleaning, as the phone app draws it.
        if (activity is RobotActivity.Vacuuming or RobotActivity.Mopping or RobotActivity.VacuumingAndMopping)
            dc.DrawGeometry(Palette.Light, null, LightCone);

        // The side brushes turn under the front of the body, only their tips showing past its rim.
        var spinning = activity is RobotActivity.Vacuuming or RobotActivity.VacuumingAndMopping;
        var turn = spinning ? BrushTurn(t) : 0;
        DrawBrush(dc, LeftBrushHub.ToWpf(), Palette.LeftBrush, turn);    // turns clockwise, sweeping inwards
        DrawBrush(dc, RightBrushHub.ToWpf(), Palette.RightBrush, -turn); // its mirror image

        dc.DrawEllipse(Palette.Body, Palette.Rim, default, BodyRadius, BodyRadius);
        dc.PushClip(BodyClip);
        dc.DrawRoundedRectangle(Palette.WaterTank, null, WaterTank.ToWpf(), 2.5, 2.5);
        dc.Pop();
        dc.DrawRoundedRectangle(Palette.Lid, null, Lid.ToWpf(), 1.5, 1.5);
        dc.DrawEllipse(Palette.Button, null, ButtonCentre.ToWpf(), ButtonRadius, ButtonRadius);

        if (activity is RobotActivity.Mopping or RobotActivity.VacuumingAndMopping) DrawRoller(dc, t);
        dc.Pop();
    }

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
        var roller = Roller.ToWpf();
        dc.DrawRoundedRectangle(Palette.Roller, null, roller, 3, 3);
        dc.PushClip(new RectangleGeometry(roller, 3, 3));
        var shift = TreadShift(t);
        for (var y = roller.Top - 5 + shift; y < roller.Bottom + TreadPitch; y += TreadPitch)
        {
            dc.DrawLine(Palette.Tread, new Point(-10, y), new Point(0, y + 2));
            dc.DrawLine(Palette.Tread, new Point(0, y + 2), new Point(10, y));
        }
        dc.Pop();
    }

    // ---- The dock ----------------------------------------------------------------

    private static void DrawPlate(DrawingContext dc, Matrix frame)
    {
        dc.PushTransform(new MatrixTransform(frame));
        dc.DrawGeometry(Palette.Plate, Palette.PlateRim, Plate);
        dc.Pop();
    }

    private static void DrawTower(DrawingContext dc, Matrix frame, DockActivity activity, double t)
    {
        dc.PushTransform(new MatrixTransform(frame));
        dc.DrawRoundedRectangle(Palette.Body, Palette.Rim, Tower.ToWpf(), 4, 4);
        dc.DrawEllipse(Palette.Bin, null, new Point(TankX[0], TankY), TankRadius, TankRadius);
        dc.DrawEllipse(Palette.BinFilter, null, new Point(TankX[0], TankY), BinFilterRadius, BinFilterRadius);
        foreach (var x in TankX.Skip(1))
            // The rim drawn inside the circle, so these tanks are no bigger than the bin, which has none.
            dc.DrawEllipse(Palette.Lid, Palette.TankRim, new Point(x, TankY), TankRadius - 0.5, TankRadius - 0.5);
        dc.DrawEllipse(Palette.DirtyDot, null, new Point(TankX[1], TankY), TankDotRadius, TankDotRadius);
        dc.DrawEllipse(Palette.CleanDot, null, new Point(TankX[2], TankY), TankDotRadius, TankDotRadius);

        switch (activity)
        {
            case DockActivity.Charging:
                dc.PushOpacity(Pulse(t, 1.4));
                dc.DrawGeometry(null, Palette.Bolt, Bolt);
                dc.Pop();
                break;
            case DockActivity.EmptyingBin:
                dc.PushTransform(new TranslateTransform(TankX[0], TankY));
                dc.PushTransform(new RotateTransform(SwirlTurn(t)));
                dc.DrawGeometry(null, Palette.Swirl, Swirl);
                dc.Pop();
                dc.Pop();
                break;
            case DockActivity.FillingWater:
                DrawTankGlow(dc, TankX[2], Palette.CleanGlow, t);
                DrawDrop(dc, Palette.CleanDrop, CleanTankOutlet.ToWpf(), RobotInlet.ToWpf(), t, 0);
                DrawDrop(dc, Palette.CleanDrop, CleanTankOutlet.ToWpf(), RobotInlet.ToWpf(), t, 0.5);
                break;
            case DockActivity.WashingRoller:
                DrawTankGlow(dc, TankX[1], Palette.DirtyGlow, t);
                DrawTankGlow(dc, TankX[2], Palette.CleanGlow, t + 0.5);
                DrawDrop(dc, Palette.DirtyDrop, RobotOutlet.ToWpf(), DirtyTankInlet.ToWpf(), t, 0);
                DrawDrop(dc, Palette.DirtyDrop, RobotOutlet.ToWpf(), DirtyTankInlet.ToWpf(), t, 0.5);
                DrawDrop(dc, Palette.CleanDrop, CleanTankOutlet.ToWpf(), RobotInlet.ToWpf(), t, 0.25);
                break;
            case DockActivity.DryingRoller:
                for (var i = 0; i < 3; i++) DrawHeatWave(dc, -6 + 6 * i, t, i * 0.45);
                break;
        }
        dc.Pop();
    }

    private static void DrawTankGlow(DrawingContext dc, double x, Brush brush, double t)
    {
        dc.PushOpacity(Pulse(t, 1));
        dc.DrawEllipse(brush, null, new Point(x, TankY), 3, 3);
        dc.Pop();
    }

    /// <summary>A drop sliding from <paramref name="from"/> to <paramref name="to"/> each second, fading in then out.</summary>
    private static void DrawDrop(DrawingContext dc, Brush brush, Point from, Point to, double t, double delay)
    {
        var phase = DropPhase(t, delay);
        dc.PushOpacity(Fade(phase));
        dc.DrawEllipse(brush, null, from + (to - from) * phase, TankDotRadius, TankDotRadius);
        dc.Pop();
    }

    /// <summary>Warm air going down from the tower to the robot on the plate.</summary>
    private static void DrawHeatWave(DrawingContext dc, double x, double t, double delay)
    {
        var phase = HeatPhase(t, delay);
        dc.PushOpacity(Fade(phase));
        dc.PushTransform(new TranslateTransform(x, TowerFront + 1 + phase * HeatTravel));
        dc.DrawGeometry(null, Palette.Heat, HeatWave);
        dc.Pop();
        dc.Pop();
    }

    // ---- Shapes and colours ------------------------------------------------------------

    private static readonly Geometry BodyClip = Frozen(new EllipseGeometry(default, BodyRadius, BodyRadius));
    private static readonly Geometry LightCone = Frozen(Geometry.Parse(LightConePath));
    private static readonly Geometry Plate = Frozen(Geometry.Parse(PlatePath));
    private static readonly Geometry Bolt = Frozen(Geometry.Parse(BoltPath));
    private static readonly Geometry Swirl = Frozen(Geometry.Parse(SwirlPath));
    private static readonly Geometry HeatWave = Frozen(Geometry.Parse(HeatWavePath));

    private static T Frozen<T>(T f) where T : Freezable { f.Freeze(); return f; }
    private static SolidColorBrush Solid(ArgbColor c) => Frozen(new SolidColorBrush(c.ToWpf()));
    private static Pen Line(ArgbColor c, double width) => Frozen(new Pen(Solid(c), width) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round });

    private static class Palette
    {
        public static readonly Brush Body = Solid(MarkerColors.Body);
        public static readonly Pen Rim = Line(MarkerColors.Rim, 1.2);
        public static readonly Brush WaterTank = Solid(MarkerColors.WaterTank);
        public static readonly Brush Lid = Solid(MarkerColors.Lid);
        public static readonly Brush Button = Solid(MarkerColors.Button);
        public static readonly Pen LeftBrush = Line(MarkerColors.LeftBrush, 1.8);
        public static readonly Pen RightBrush = Line(MarkerColors.RightBrush, 1.8);
        public static readonly Brush Roller = Solid(MarkerColors.Roller);
        public static readonly Pen Tread = Line(MarkerColors.Tread, 0.9);
        /// <summary>Bright at the robot, fading out ahead of it.</summary>
        public static readonly Brush Light = Frozen(new LinearGradientBrush(
            new GradientStopCollection { new(MarkerColors.LightNear.ToWpf(), 0), new(MarkerColors.LightFar.ToWpf(), 1) },
            new Point(0, 0), new Point(0, 1)) { MappingMode = BrushMappingMode.Absolute, StartPoint = new Point(0, LightNear), EndPoint = new Point(0, LightFar) });
        public static readonly Brush Plate = Solid(MarkerColors.Plate);
        public static readonly Pen PlateRim = Line(MarkerColors.PlateRim, 1);
        public static readonly Brush Bin = Solid(MarkerColors.Bin);
        public static readonly Brush BinFilter = Solid(MarkerColors.BinFilter);
        public static readonly Pen TankRim = Line(MarkerColors.TankRim, 1);
        public static readonly Brush DirtyDot = Solid(MarkerColors.DirtyDot);
        public static readonly Brush CleanDot = Solid(MarkerColors.CleanDot);
        public static readonly Brush DirtyGlow = Solid(MarkerColors.DirtyGlow);
        public static readonly Brush CleanGlow = Solid(MarkerColors.CleanGlow);
        public static readonly Brush DirtyDrop = Solid(MarkerColors.DirtyDrop);
        public static readonly Brush CleanDrop = Solid(MarkerColors.CleanDrop);
        public static readonly Pen Bolt = Line(MarkerColors.Bolt, 1.6);
        public static readonly Pen Swirl = Line(MarkerColors.Swirl, 1.4);
        public static readonly Pen Heat = Line(MarkerColors.Heat, 1.4);
    }
}
