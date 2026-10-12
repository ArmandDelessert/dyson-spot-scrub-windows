using DyssCockpit.Core;
using DyssCockpit.Presentation.Map;

namespace DyssCockpit.Presentation.Tests;

/// <summary>
/// Where the robot and its dock are drawn, how big and facing which way: what every renderer of
/// the map shares. The screen here is the world at 100 px a metre, not flipped, so a frame's
/// origin is simply the world point times 100.
/// </summary>
public class RobotMarkerLayoutTests
{
    private static readonly MapTransform Close = MapTransform.Identity.Scaled(100, 100);

    private static RobotPosition At(double x, double y, double angle = 0) => new(1, x, y, angle, null);

    private static Vec2 Centre(MarkerPlacement p) => p.Frame.Transform(0, 0);

    private static double Distance(Vec2 a, Vec2 b) => (a - b).Length;

    [Fact]
    public void ARobotAwayFromItsDockIsDrawnLastWhereItIs()
    {
        var scene = new MapScene { Dock = new DockLocation(0, 0, 0), Robot = At(2, 1), RobotActivity = RobotActivity.Vacuuming };

        var placements = RobotMarkerLayout.Place(scene, Close);

        Assert.Equal([MarkerPart.DockPlate, MarkerPart.DockTower, MarkerPart.Robot], placements.Select(p => p.Part));
        Assert.Equal(new Vec2(200, 100), Centre(placements[2]));
        Assert.Equal(RobotActivity.Vacuuming, placements[2].Robot);
    }

    [Fact]
    public void ADockedRobotSitsAheadOfTheDockBetweenThePlateAndTheTower()
    {
        // Where it reported itself does not matter once docked: the dock says where it is.
        var scene = new MapScene { Dock = new DockLocation(1, 1, 0), Robot = At(5, 5), RobotDocked = true, DockActivity = DockActivity.Charging };

        var placements = RobotMarkerLayout.Place(scene, Close);

        Assert.Equal([MarkerPart.DockPlate, MarkerPart.Robot, MarkerPart.DockTower], placements.Select(p => p.Part));
        Assert.Equal(100 * (1 + RobotMarkerLayout.DockedOffset), Centre(placements[1]).X, 9);
        Assert.Equal(100, Centre(placements[1]).Y, 9);
        Assert.Equal(DockActivity.Charging, placements[2].Dock);
    }

    [Fact]
    public void AFarAwaySentinelDockIsNotDrawnAndTheRobotStaysWhereItIs()
    {
        var scene = new MapScene { Dock = new DockLocation(1100, 1100, 0), Robot = At(2, 1), RobotDocked = true };

        var robot = Assert.Single(RobotMarkerLayout.Place(scene, Close));

        Assert.Equal(new Vec2(200, 100), Centre(robot));
    }

    [Fact]
    public void TheGlidingPositionWinsOverTheReportedOne()
    {
        var scene = new MapScene { Robot = At(2, 1) };

        var robot = Assert.Single(RobotMarkerLayout.Place(scene, Close, robotAt: At(3, 3)));

        Assert.Equal(new Vec2(300, 300), Centre(robot));
    }

    [Fact]
    public void TheRobotIsDrawnAtItsRealSizeUntilThatIsTooSmallToRead()
    {
        var scene = new MapScene { Robot = At(0, 0) };
        var rim = new Vec2(RobotMarkerLayout.Units, 0);

        var close = Assert.Single(RobotMarkerLayout.Place(scene, Close));
        Assert.Equal(RobotMarkerLayout.RobotRadius * 100, Distance(close.Frame.Transform(rim), Centre(close)), 9);

        // At 10 px a metre the robot would be under 2 px across: it is kept at 11 px of radius.
        var far = Assert.Single(RobotMarkerLayout.Place(scene, MapTransform.Identity.Scaled(10, 10)));
        Assert.Equal(11, Distance(far.Frame.Transform(rim), Centre(far)), 9);
    }

    [Theory]
    [InlineData(0, 1, 0)]
    [InlineData(Math.PI / 2, 0, 1)]
    [InlineData(Math.PI, -1, 0)]
    public void TheFrontOfTheIconFacesTheWayTheRobotIsHeading(double angle, double x, double y)
    {
        var scene = new MapScene { Robot = At(0, 0, angle) };

        var robot = Assert.Single(RobotMarkerLayout.Place(scene, Close));
        var front = robot.Frame.Transform(0, -RobotMarkerLayout.Units);   // the icon's front is at -y

        Assert.Equal(x * RobotMarkerLayout.RobotRadius * 100, front.X, 9);
        Assert.Equal(y * RobotMarkerLayout.RobotRadius * 100, front.Y, 9);
    }

    [Fact]
    public void OnlyWhatIsAtWorkKeepsTheMapRedrawing()
    {
        Assert.True(RobotMarkerLayout.IsAnimated(new MapScene { Robot = At(0, 0), RobotActivity = RobotActivity.Mopping }));
        // Driving is shown by the robot moving on the map, not by its brushes.
        Assert.False(RobotMarkerLayout.IsAnimated(new MapScene { Robot = At(0, 0), RobotActivity = RobotActivity.Moving }));
        Assert.False(RobotMarkerLayout.IsAnimated(new MapScene { Robot = At(0, 0), RobotActivity = RobotActivity.Vacuuming, RobotDocked = true }));
        Assert.True(RobotMarkerLayout.IsAnimated(new MapScene { Dock = new DockLocation(0, 0, 0), DockActivity = DockActivity.EmptyingBin }));
        Assert.False(RobotMarkerLayout.IsAnimated(new MapScene { Dock = new DockLocation(0, 0, 0) }));
    }

    [Fact]
    public void TheAnimationsStayInTheirRanges()
    {
        // A pulse breathes between 0.25 and 1.
        Assert.Equal(1, RobotMarkerLayout.Pulse(0, 2), 9);
        Assert.Equal(0.25, RobotMarkerLayout.Pulse(1, 2), 9);

        // Phases wrap into [0, 1), even before their delay has passed.
        Assert.Equal(0.75, RobotMarkerLayout.DropPhase(0, 0.25), 9);
        Assert.Equal(0.5, RobotMarkerLayout.HeatPhase(2.1, 0), 9);

        // In over the first 30 %, then out.
        Assert.Equal(0, RobotMarkerLayout.Fade(0), 9);
        Assert.Equal(1, RobotMarkerLayout.Fade(0.3), 9);
        Assert.Equal(0, RobotMarkerLayout.Fade(1), 9);

        // The treads run within one pitch; a brush turns a full turn in 0.8 s.
        foreach (var t in (double[])[0, 0.1, 0.34, 0.36, 7.9])
            Assert.InRange(RobotMarkerLayout.TreadShift(t), 0, RobotMarkerLayout.TreadPitch);
        Assert.Equal(360, RobotMarkerLayout.BrushTurn(0.8), 9);
    }
}
