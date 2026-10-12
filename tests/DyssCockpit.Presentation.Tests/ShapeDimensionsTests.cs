using System.Text.Json;
using DyssCockpit.Core;
using DyssCockpit.Presentation.Map;
using DyssCockpit.Presentation.Services;
using DyssCockpit.Presentation.ViewModels;

namespace DyssCockpit.Presentation.Tests;

/// <summary>
/// The size of a rectangle drawn on the map, written around it when the setting asks for it, and
/// how a zone clean reads in the history.
/// </summary>
public class ShapeDimensionsTests
{
    /// <summary>100 px a metre, y up in the world and down on screen.</summary>
    private static readonly MapTransform Upright = MapTransform.Identity.Scaled(100, -100).Translated(0, 1000);

    /// <summary>From (1, 2) to (3.5, 3): 2.50 m wide, 1.00 m high, corners top-right first.</summary>
    private static Vec2[] Rectangle() => [.. SpotCleanSequence.Corners(new(1, 2), new(3.5, 3)).Select(Vec2.Of)];

    // ---- The labels ----------------------------------------------------------------------------

    [Fact]
    public void EachCornerHasItsCoordinatesAndTheTopAndLeftSidesTheirLength()
    {
        var labels = MapGeometry.DimensionLabels(Rectangle(), Upright);

        Assert.Equal(["x 3,50  y 3,00 m", "x 1,00  y 3,00 m", "x 1,00  y 2,00 m", "x 3,50  y 2,00 m", "2,50 m", "1,00 m"], labels.Select(l => l.Text));
        // Each corner's label stands off it, away from the middle of the rectangle.
        Assert.Equal([new(1, -1), new(-1, -1), new(-1, 1), new(1, 1)], labels.Take(4).Select(l => l.Away));
        Assert.Equal(new Vec2(350, 700), labels[0].At);
        // The width above the top side, the height left of the left side, both at their middle.
        Assert.Equal((new Vec2(225, 700), new Vec2(0, -1)), (labels[4].At, labels[4].Away));
        Assert.Equal((new Vec2(100, 750), new Vec2(-1, 0)), (labels[5].At, labels[5].Away));
    }

    [Fact]
    public void OnATurnedMapTheSidesAreTakenAsTheyStandOnScreen()
    {
        // A quarter turn: the 1 m side is now on top, the 2.50 m one on the left.
        var turned = MapTransform.Identity.Scaled(100, -100).Rotated(90).Translated(1000, 1000);

        var labels = MapGeometry.DimensionLabels(Rectangle(), turned);

        Assert.Equal(["1,00 m", "2,50 m"], labels.Skip(4).Select(l => l.Text));
    }

    [Fact]
    public void LengthsAreWrittenInTheLanguagesNotation()
    {
        Assert.Equal("2,50 m", MapGeometry.LengthText(2.5));
        Assert.Equal("3,00 × 0,90 m", MapGeometry.SizeText(3, 0.9));
    }

    [Fact]
    public void FewerThanFourCornersHaveNoLabels() =>
        Assert.Empty(MapGeometry.DimensionLabels([new(0, 0), new(1, 1)], Upright));

    // ---- On the map -----------------------------------------------------------------------------

    private static MapInteraction Map() => new()
    {
        Scene = new MapScene { Grid = new MapGrid(new MapDimensions(8, 8, 0.5, 0, 0), [.. Enumerable.Repeat(10, 64)]) },
        Size = new Size2(400, 400),
    };

    private static void Click(MapInteraction map, Vec2 at)
    {
        map.PointerPressed(at);
        map.PointerReleased(at, clickCount: 1);
    }

    [Fact]
    public void ARectangleBeingDrawnIsMeasuredOnlyWhenTheSettingSaysSo()
    {
        var map = Map();
        map.Picking = MapPick.Rectangle;
        Click(map, map.WorldToScreen.Transform(1, 2));
        map.PointerMoved(map.WorldToScreen.Transform(3.5, 3), pressed: false);
        Assert.Null(map.Overlay.Dimensions);

        map.ShowShapeDimensions = true;

        var labels = map.Overlay.Dimensions!;
        Assert.Equal(6, labels.Count);
        Assert.Equal(["2,50 m", "1,00 m"], labels.Skip(4).Select(l => l.Text));
    }

    [Fact]
    public void TheZoneToCleanIsMeasuredToo()
    {
        var map = Map();
        map.ShowShapeDimensions = true;
        map.EditableShapeResizable = true;

        map.EditableShape = SpotCleanSequence.Corners(new(1, 2), new(3.5, 3));

        Assert.Equal("x 3,50  y 3,00 m", map.Overlay.Dimensions![0].Text);
    }

    [Fact]
    public void TheSettingIsOffByDefaultAndRemembered()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dyss-settings-{Guid.NewGuid():N}.json");
        try
        {
            var first = AppSettings.Load(path);
            Assert.False(first.ShowShapeDimensions);
            first.ShowShapeDimensions = true;
            Assert.True(AppSettings.Load(path).ShowShapeDimensions);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ---- In the history ----------------------------------------------------------------------------

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// The detail of a zone clean on 2026-10-12: it still marks the Couloir, the room of the robot's last
    /// room task, as CLEAN_PENDING, though the clean was a rectangle in the bathroom.
    /// </summary>
    private static readonly CleanDetail ZoneDetail = JsonSerializer.Deserialize<CleanDetail>("""
        {"cleanId":"c","zones":[{"id":"11","name":"Salle de bain","type":"bathroom","cleanStatus":"CLEAN_NOT_REQUESTED"},
                                {"id":"13","name":"Couloir","type":"hallway","cleanStatus":"CLEAN_PENDING"}]}
        """, Web)!;

    private static CleanSummary Summary(string spotZones) => JsonSerializer.Deserialize<CleanSummary>(
        $$"""{"cleanId":"c","isSpotClean":true,"spotZones":{{spotZones}}}""", Web)!;

    [Fact]
    public void AZoneCleanIsToldByItsSizeRatherThanByARoom()
    {
        var summary = Summary("""[{"id":"0","points":[{"x":3.6,"y":5.1},{"x":1.1,"y":5.1},{"x":1.1,"y":3.6},{"x":3.6,"y":3.6}]}]""");

        Assert.Equal("Zone 2,50 × 1,50 m", HistoryViewModel.DescribeRooms(summary, ZoneDetail));
    }

    [Fact]
    public void SeveralZonesAreCountedAndEachMeasured()
    {
        var summary = Summary("""
            [{"id":"0","points":[{"x":3.7,"y":2.7},{"x":0.7,"y":2.7},{"x":0.7,"y":1.8},{"x":3.7,"y":1.8}]},
             {"id":"1","points":[{"x":2.45,"y":-2.75},{"x":1.7,"y":-2.75},{"x":1.7,"y":-5.6},{"x":2.45,"y":-5.6}]}]
            """);

        Assert.Equal("2 zones : 3,00 × 0,90 m, 0,75 × 2,85 m", HistoryViewModel.DescribeRooms(summary, ZoneDetail));
    }

    [Fact]
    public void AZoneCleanWithoutItsRectanglesIsStillAZone() =>
        Assert.Equal("Zone", HistoryViewModel.DescribeRooms(Summary("null"), ZoneDetail));

    [Fact]
    public void ARoomCleanStillListsItsRooms()
    {
        var summary = JsonSerializer.Deserialize<CleanSummary>("""{"cleanId":"c","isSpotClean":false}""", Web)!;

        Assert.Equal("Couloir", HistoryViewModel.DescribeRooms(summary, ZoneDetail));
    }
}
