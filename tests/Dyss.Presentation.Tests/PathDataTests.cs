using Dyss.Presentation.Map;

namespace Dyss.Presentation.Tests;

public class PathDataTests
{
    [Fact]
    public void TheDockPlateIsOneClosedFigureOfLinesAndAnArc()
    {
        var figure = Assert.Single(PathData.Parse(RobotMarkerLayout.PlatePath));

        Assert.True(figure.Closed);
        Assert.Equal(new Vec2(-22, 0), figure.Start);
        Assert.Equal(new Vec2(22, 0), Assert.IsType<LineSegment>(figure.Segments[0]).End);
        // H and V keep the other coordinate.
        Assert.Equal(new Vec2(22, 35.5), Assert.IsType<LineSegment>(figure.Segments[1]).End);
        var arc = Assert.IsType<ArcSegment>(figure.Segments[2]);
        Assert.Equal(new ArcSegment(new Vec2(-22, 35.5), 22, 10, 0, LargeArc: false, Clockwise: true), arc);
    }

    [Fact]
    public void ANewMoveStartsAnotherOpenFigure()
    {
        var figures = PathData.Parse(RobotMarkerLayout.SwirlPath);

        Assert.Equal(2, figures.Count);
        Assert.All(figures, f => Assert.False(f.Closed));
        Assert.Equal(new Vec2(0, 3.6), figures[1].Start);
    }

    [Fact]
    public void NumbersNeedNoSpaceBeforeAMinusSign()
    {
        var figure = Assert.Single(PathData.Parse("M-6,-14 L-13-31 Q0,-35 13,-31 Z"));

        Assert.Equal(new Vec2(-13, -31), figure.Segments[0].End);
        Assert.Equal(new QuadraticSegment(new Vec2(0, -35), new Vec2(13, -31)), figure.Segments[1]);
    }

    [Fact]
    public void EveryIconShapeParses()
    {
        foreach (var path in new[] { RobotMarkerLayout.LightConePath, RobotMarkerLayout.PlatePath, RobotMarkerLayout.BoltPath, RobotMarkerLayout.SwirlPath, RobotMarkerLayout.HeatWavePath })
            Assert.NotEmpty(PathData.Parse(path));
    }
}
