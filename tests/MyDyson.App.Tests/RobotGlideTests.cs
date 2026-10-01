using System.Windows.Media;
using MyDyson.App.Rendering;
using MyDyson.Core;

namespace MyDyson.App.Tests;

public class RobotGlideTests
{
    private static RobotPosition At(double x, double y, double angle = 0) => new(1, x, y, angle, 1);

    [Fact]
    public void TheFirstPositionIsShownAtOnce()
    {
        var glide = new RobotGlide();
        Assert.Equal(At(1, 2), glide.At(At(1, 2), now: 10));
        Assert.False(glide.IsGliding(10));
    }

    [Fact]
    public void ANewPositionIsReachedOverTheTimeTheLastOneTookToCome()
    {
        var glide = new RobotGlide();
        glide.At(At(0, 0), now: 0);
        glide.At(At(0, 0), now: 1);          // no news: stays put
        var start = glide.At(At(1, 0), now: 1);   // a second after the first position
        Assert.Equal(0, start.X, 6);
        Assert.True(glide.IsGliding(1.2));

        Assert.Equal(0.5, glide.At(At(1, 0), now: 1.5).X, 6);
        Assert.Equal(1, glide.At(At(1, 0), now: 2.5).X, 6);
        Assert.False(glide.IsGliding(2.5));
    }

    [Fact]
    public void ANewPositionMidwayStartsFromWhereTheRobotIsShown()
    {
        var glide = new RobotGlide();
        glide.At(At(0, 0), now: 0);
        glide.At(At(1, 0), now: 1);
        // Halfway there, the robot reports again: the glide continues from x = 0.5, not from 1.
        Assert.Equal(0.5, glide.At(At(1, 1), now: 1.5).X, 6);
        Assert.Equal(0.75, glide.At(At(1, 1), now: 1.75).X, 6);
    }

    [Fact]
    public void ItTurnsTheShortWayAndJumpsWhenTooFar()
    {
        var glide = new RobotGlide();
        glide.At(At(0, 0, 3.0), now: 0);
        // From 3.0 rad to -3.0 rad is 0.28 rad through pi, not 6 rad the long way round.
        var half = glide.At(At(0.1, 0, -3.0), now: 1);
        half = glide.At(At(0.1, 0, -3.0), now: 1.5);
        Assert.InRange(half.Angle, 3.0, 3.3);

        var far = glide.At(At(5, 5), now: 3);
        Assert.Equal(5, far.X);
    }

    [Fact]
    public void ObstaclesGrowWithTheZoomBetweenTwoBounds()
    {
        Assert.Equal(8, MapRenderer.ObstacleMarkerSize(new Matrix(20, 0, 0, -20, 0, 0)));
        Assert.Equal(12, MapRenderer.ObstacleMarkerSize(new Matrix(150, 0, 0, -150, 0, 0)), 6);
        Assert.Equal(16, MapRenderer.ObstacleMarkerSize(new Matrix(800, 0, 0, -800, 0, 0)));
    }
}
