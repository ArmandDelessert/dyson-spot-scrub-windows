using System.Numerics;
using System.Runtime.CompilerServices;
using DyssCockpit.Core;
using DyssCockpit.Presentation.Map;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Geometry;
using Windows.UI;
using static DyssCockpit.Presentation.Map.RobotMarkerLayout;
using MarkerColors = DyssCockpit.Presentation.Map.RobotMarkerLayout.Colors;

namespace DyssCockpit.App.Rendering;

/// <summary>
/// Draws the robot and its dock as <see cref="RobotMarkerLayout"/> lays them out: where, how big, in
/// what colours and moving how is decided there; this turns it into Win2D drawing calls. Each part
/// is drawn in its icon units through its frame, so line widths scale with the icon.
/// </summary>
internal static class RobotMarkers
{
    /// <summary>
    /// Draws the dock, then the robot, at <paramref name="seconds"/> into their animations. The robot
    /// stands at <paramref name="robotAt"/> when given (see <see cref="RobotGlide"/>), else where it reported itself.
    /// </summary>
    public static void Draw(CanvasDrawingSession ds, MapScene scene, MapTransform worldToScreen, double seconds, RobotPosition? robotAt = null)
    {
        foreach (var marker in Place(scene, worldToScreen, robotAt))
        {
            var frame = marker.Frame.ToMatrix();
            switch (marker.Part)
            {
                case MarkerPart.DockPlate: DrawPlate(ds, frame); break;
                case MarkerPart.DockTower: DrawTower(ds, frame, marker.Dock, seconds); break;
                case MarkerPart.Robot: DrawRobot(ds, frame, marker.Robot, seconds); break;
            }
        }
    }

    /// <summary>The robot as the application's icon: cleaning, its light on, brushes at rest. See <see cref="AppIcon"/>.</summary>
    public static void DrawIcon(CanvasDrawingSession ds, Matrix3x2 frame) => DrawRobot(ds, frame, RobotActivity.Vacuuming, 0);

    /// <summary>Draws in the coordinates of <paramref name="frame"/> until disposed, as WPF's PushTransform.</summary>
    private static TransformScope In(CanvasDrawingSession ds, Matrix3x2 frame) => new(ds, frame);

    private readonly struct TransformScope : IDisposable
    {
        private readonly CanvasDrawingSession _ds;
        private readonly Matrix3x2 _saved;

        public TransformScope(CanvasDrawingSession ds, Matrix3x2 frame)
        {
            _ds = ds;
            _saved = ds.Transform;
            ds.Transform = frame * _saved;
        }

        public void Dispose() => _ds.Transform = _saved;
    }

    // ---- The robot ---------------------------------------------------------------

    private static void DrawRobot(CanvasDrawingSession ds, Matrix3x2 frame, RobotActivity activity, double t)
    {
        var shapes = Shapes.For(ds);
        using var _ = In(ds, frame);
        // The green light it shines on the floor ahead while cleaning, as the phone app draws it.
        if (activity is RobotActivity.Vacuuming or RobotActivity.Mopping or RobotActivity.VacuumingAndMopping)
        {
            using var light = new CanvasLinearGradientBrush(ds, MarkerColors.LightNear.ToColor(), MarkerColors.LightFar.ToColor())
            {
                StartPoint = new Vector2(0, (float)LightNear),
                EndPoint = new Vector2(0, (float)LightFar),
            };
            ds.FillGeometry(shapes.LightCone, light);
        }

        // The side brushes turn under the front of the body, only their tips showing past its rim.
        var spinning = activity is RobotActivity.Vacuuming or RobotActivity.VacuumingAndMopping;
        var turn = spinning ? BrushTurn(t) : 0;
        DrawBrush(ds, LeftBrushHub.ToVector2(), MarkerColors.LeftBrush.ToColor(), turn);     // turns clockwise, sweeping inwards
        DrawBrush(ds, RightBrushHub.ToVector2(), MarkerColors.RightBrush.ToColor(), -turn);  // its mirror image

        var body = (float)BodyRadius;
        ds.FillCircle(Vector2.Zero, body, MarkerColors.Body.ToColor());
        ds.DrawCircle(Vector2.Zero, body, MarkerColors.Rim.ToColor(), 1.2f);
        using (ds.CreateLayer(1, shapes.BodyClip))
            ds.FillRoundedRectangle(WaterTank.ToRect(), 2.5f, 2.5f, MarkerColors.WaterTank.ToColor());
        ds.FillRoundedRectangle(Lid.ToRect(), 1.5f, 1.5f, MarkerColors.Lid.ToColor());
        ds.FillCircle(ButtonCentre.ToVector2(), (float)ButtonRadius, MarkerColors.Button.ToColor());

        if (activity is RobotActivity.Mopping or RobotActivity.VacuumingAndMopping) DrawRoller(ds, t);
    }

    private static void DrawBrush(CanvasDrawingSession ds, Vector2 hub, Color colour, double degrees)
    {
        using var _ = In(ds, Matrix3x2.CreateRotation((float)(degrees * Math.PI / 180), hub));
        for (var arm = 0; arm < 3; arm++)
        {
            var a = (arm * 120 - 90) * Math.PI / 180;
            var tip = new Vector2(hub.X + (float)(BrushArm * Math.Cos(a)), hub.Y + (float)(BrushArm * Math.Sin(a)));
            ds.DrawLine(hub, tip, colour, 1.8f, Shapes.Round);
        }
    }

    /// <summary>The roller across the back, its treads running backwards like a tractor tyre's.</summary>
    private static void DrawRoller(CanvasDrawingSession ds, double t)
    {
        var roller = Roller.ToRect();
        ds.FillRoundedRectangle(roller, 3, 3, MarkerColors.Roller.ToColor());
        using var clip = CanvasGeometry.CreateRoundedRectangle(ds, roller, 3, 3);
        using var _ = ds.CreateLayer(1, clip);
        var tread = MarkerColors.Tread.ToColor();
        var shift = TreadShift(t);
        for (var y = Roller.Top - 5 + shift; y < Roller.Bottom + TreadPitch; y += TreadPitch)
        {
            ds.DrawLine(-10, (float)y, 0, (float)y + 2, tread, 0.9f, Shapes.Round);
            ds.DrawLine(0, (float)y + 2, 10, (float)y, tread, 0.9f, Shapes.Round);
        }
    }

    // ---- The dock ----------------------------------------------------------------

    private static void DrawPlate(CanvasDrawingSession ds, Matrix3x2 frame)
    {
        var shapes = Shapes.For(ds);
        using var _ = In(ds, frame);
        ds.FillGeometry(shapes.Plate, MarkerColors.Plate.ToColor());
        ds.DrawGeometry(shapes.Plate, MarkerColors.PlateRim.ToColor(), 1, Shapes.Round);
    }

    private static void DrawTower(CanvasDrawingSession ds, Matrix3x2 frame, DockActivity activity, double t)
    {
        var shapes = Shapes.For(ds);
        using var _ = In(ds, frame);
        var (tankY, radius) = ((float)TankY, (float)TankRadius);
        ds.FillRoundedRectangle(Tower.ToRect(), 4, 4, MarkerColors.Body.ToColor());
        ds.DrawRoundedRectangle(Tower.ToRect(), 4, 4, MarkerColors.Rim.ToColor(), 1.2f, Shapes.Round);
        ds.FillCircle((float)TankX[0], tankY, radius, MarkerColors.Bin.ToColor());
        ds.FillCircle((float)TankX[0], tankY, (float)BinFilterRadius, MarkerColors.BinFilter.ToColor());
        foreach (var x in TankX.Skip(1))
        {
            // The rim drawn inside the circle, so these tanks are no bigger than the bin, which has none.
            ds.FillCircle((float)x, tankY, radius - 0.5f, MarkerColors.Lid.ToColor());
            ds.DrawCircle((float)x, tankY, radius - 0.5f, MarkerColors.TankRim.ToColor(), 1, Shapes.Round);
        }
        ds.FillCircle((float)TankX[1], tankY, (float)TankDotRadius, MarkerColors.DirtyDot.ToColor());
        ds.FillCircle((float)TankX[2], tankY, (float)TankDotRadius, MarkerColors.CleanDot.ToColor());

        switch (activity)
        {
            case DockActivity.Charging:
                using (ds.CreateLayer((float)Pulse(t, 1.4)))
                    ds.DrawGeometry(shapes.Bolt, MarkerColors.Bolt.ToColor(), 1.6f, Shapes.Round);
                break;
            case DockActivity.EmptyingBin:
                using (In(ds, Matrix3x2.CreateRotation((float)(SwirlTurn(t) * Math.PI / 180)) * Matrix3x2.CreateTranslation((float)TankX[0], tankY)))
                    ds.DrawGeometry(shapes.Swirl, MarkerColors.Swirl.ToColor(), 1.4f, Shapes.Round);
                break;
            case DockActivity.FillingWater:
                DrawTankGlow(ds, TankX[2], MarkerColors.CleanGlow, t);
                DrawDrop(ds, MarkerColors.CleanDrop, CleanTankOutlet, RobotInlet, t, 0);
                DrawDrop(ds, MarkerColors.CleanDrop, CleanTankOutlet, RobotInlet, t, 0.5);
                break;
            case DockActivity.WashingRoller:
                DrawTankGlow(ds, TankX[1], MarkerColors.DirtyGlow, t);
                DrawTankGlow(ds, TankX[2], MarkerColors.CleanGlow, t + 0.5);
                DrawDrop(ds, MarkerColors.DirtyDrop, RobotOutlet, DirtyTankInlet, t, 0);
                DrawDrop(ds, MarkerColors.DirtyDrop, RobotOutlet, DirtyTankInlet, t, 0.5);
                DrawDrop(ds, MarkerColors.CleanDrop, CleanTankOutlet, RobotInlet, t, 0.25);
                break;
            case DockActivity.DryingRoller:
                for (var i = 0; i < 3; i++) DrawHeatWave(ds, shapes, -6 + 6 * i, t, i * 0.45);
                break;
        }
    }

    private static void DrawTankGlow(CanvasDrawingSession ds, double x, ArgbColor colour, double t)
    {
        using var _ = ds.CreateLayer((float)Pulse(t, 1));
        ds.FillCircle((float)x, (float)TankY, 3, colour.ToColor());
    }

    /// <summary>A drop sliding from <paramref name="from"/> to <paramref name="to"/> each second, fading in then out.</summary>
    private static void DrawDrop(CanvasDrawingSession ds, ArgbColor colour, Vec2 from, Vec2 to, double t, double delay)
    {
        var phase = DropPhase(t, delay);
        using var _ = ds.CreateLayer((float)Fade(phase));
        ds.FillCircle((from + (to - from) * phase).ToVector2(), (float)TankDotRadius, colour.ToColor());
    }

    /// <summary>Warm air going down from the tower to the robot on the plate.</summary>
    private static void DrawHeatWave(CanvasDrawingSession ds, Shapes shapes, double x, double t, double delay)
    {
        var phase = HeatPhase(t, delay);
        using var layer = ds.CreateLayer((float)Fade(phase));
        using var _ = In(ds, Matrix3x2.CreateTranslation((float)x, (float)(TowerFront + 1 + phase * HeatTravel)));
        ds.DrawGeometry(shapes.HeatWave, MarkerColors.Heat.ToColor(), 1.4f, Shapes.Round);
    }

    /// <summary>The icon's outlines, built once per graphics device from <see cref="RobotMarkerLayout"/>'s path data.</summary>
    private sealed class Shapes
    {
        public static readonly CanvasStrokeStyle Round = new() { StartCap = CanvasCapStyle.Round, EndCap = CanvasCapStyle.Round, LineJoin = CanvasLineJoin.Round };
        private static readonly ConditionalWeakTable<CanvasDevice, Shapes> ByDevice = [];

        public readonly CanvasGeometry BodyClip, LightCone, Plate, Bolt, Swirl, HeatWave;

        private Shapes(ICanvasResourceCreator device)
        {
            BodyClip = CanvasGeometry.CreateCircle(device, Vector2.Zero, (float)BodyRadius);
            LightCone = PathData.Parse(LightConePath).ToGeometry(device);
            Plate = PathData.Parse(PlatePath).ToGeometry(device);
            Bolt = PathData.Parse(BoltPath).ToGeometry(device);
            Swirl = PathData.Parse(SwirlPath).ToGeometry(device);
            HeatWave = PathData.Parse(HeatWavePath).ToGeometry(device);
        }

        public static Shapes For(CanvasDrawingSession ds) => ByDevice.GetValue(ds.Device, device => new Shapes(device));
    }
}
