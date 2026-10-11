using DyssCockpit.Core;
using DyssCockpit.Presentation.Map;
using DyssCockpit.Presentation.Services;

namespace DyssCockpit.Presentation.Tests;

/// <summary>The map coordinates written above the pointer, when the setting asks for them.</summary>
public class PointerCoordinatesTests
{
    /// <summary>A 2 m square on a 50 cm grid.</summary>
    private static MapInteraction Map() => new()
    {
        Scene = new MapScene { Grid = new MapGrid(new MapDimensions(4, 4, 0.5, 0, 0), [.. Enumerable.Repeat(10, 16)]) },
        Size = new Size2(400, 400),
    };

    [Fact]
    public void OffNothingIsWrittenWhereverThePointerGoes()
    {
        var map = Map();

        map.PointerMoved(map.WorldToScreen.Transform(1, 1), pressed: false);

        Assert.Null(map.Overlay.PointerCoordinates);
    }

    [Fact]
    public void OnThePointersMapPositionIsWrittenWhereItIsAndGoesWhenItLeaves()
    {
        var map = Map();
        map.ShowPointerCoordinates = true;
        var redraws = 0;
        map.Invalidated += () => redraws++;
        var screen = map.WorldToScreen.Transform(1.25, 0.5);

        map.PointerMoved(screen, pressed: false);

        var (at, world) = map.Overlay.PointerCoordinates!.Value;
        Assert.Equal(screen, at);
        Assert.Equal(1.25, world.X, 6);
        Assert.Equal(0.5, world.Y, 6);
        Assert.True(redraws > 0);

        map.PointerExited();
        Assert.Null(map.Overlay.PointerCoordinates);
    }

    [Fact]
    public void TurnedOffWhileShownTheLabelGoesAtOnce()
    {
        var map = Map();
        map.ShowPointerCoordinates = true;
        map.PointerMoved(map.WorldToScreen.Transform(1, 1), pressed: false);

        map.ShowPointerCoordinates = false;

        Assert.Null(map.Overlay.PointerCoordinates);
    }

    [Fact]
    public void WhileDrawingOnTheGridTheCoordinatesAreThoseOfTheSnappedPoint()
    {
        var map = Map();
        map.ShowPointerCoordinates = true;
        map.SnapToGrid = true;
        map.Picking = MapPick.Rectangle;

        map.PointerMoved(map.WorldToScreen.Transform(1.2, 0.6), pressed: false);

        var (_, world) = map.Overlay.PointerCoordinates!.Value;
        Assert.Equal(1.0, world.X, 6);   // the 50 cm cell the click would land on
        Assert.Equal(0.5, world.Y, 6);
    }

    [Fact]
    public void TheTextHasTwoDecimalsTheLanguagesDecimalMarkAndATrueMinus()
    {
        Assert.Equal("x 1,44  y −0,12 m", MapGeometry.CoordinatesText(new Vec2(1.4449, -0.1234)));
    }

    [Fact]
    public void TheSettingIsOffByDefaultAndRemembered()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dyss-settings-{Guid.NewGuid():N}.json");
        try
        {
            var first = AppSettings.Load(path);
            Assert.False(first.ShowPointerCoordinates);
            first.ShowPointerCoordinates = true;
            Assert.True(AppSettings.Load(path).ShowPointerCoordinates);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
