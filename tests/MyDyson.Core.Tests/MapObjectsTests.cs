using System.Text.Json.Nodes;
using MyDyson.Core;

namespace MyDyson.Core.Tests;

/// <summary>
/// Restriction zones and furniture, checked against what the phone app sent on 2026-09-23 (map id
/// anonymised) and against the geometry the map manager relies on.
/// </summary>
public class MapObjectsTests
{
    private const long MapId = 1000000002;

    [Fact]
    public async Task ZonesGoOutAsTheWholeListCountFirst()
    {
        // The second set_virtual_wall of the 23 September capture: an "Éviter la zone" drawn first,
        // then a "Franchir le seuil".
        var robot = new Recorder("""{"msgId":"1","code":0,"data":{"map_id":1000000002,"map_type":0,"timestamp":0,"map_id":1000000002,"map_type":13,"timestamp":0}}""");
        RestrictionZone[] zones =
        [
            new(RestrictionKind.FromJdm(2)!, [new(1.3867855072021484, 1.3499996662139893), new(1.3867855072021484, 0.05999958515167236),
                                              new(2.5704925060272217, 0.05999958515167236), new(2.5704925060272217, 1.3499996662139893)]),
            new(RestrictionKind.FromJdm(13)!, [new(-0.023610234260559082, 0.20370972156524658), new(-0.023610234260559082, -0.891735315322876),
                                               new(1.0718350410461426, -0.891735315322876), new(1.0718350410461426, 0.20370972156524658)]),
        ];

        var result = await robot.SetRestrictionsAsync(MapId, zones);

        Assert.NotNull(result);   // despite the duplicated keys in the reply
        Assert.Equal("service.set_virtual_wall", robot.Sent.Single().Method);
        AssertJson("""
            {"virwall":[2,
              [1000000002,2,1.3867855072021484,1.3499996662139893,1.3867855072021484,0.05999958515167236,2.5704925060272217,0.05999958515167236,2.5704925060272217,1.3499996662139893],
              [1000000002,13,-0.023610234260559082,0.20370972156524658,-0.023610234260559082,-0.891735315322876,1.0718350410461426,-0.891735315322876,1.0718350410461426,0.20370972156524658]]}
            """, robot.Sent[0].Payload);
    }

    [Fact]
    public async Task NoZoneLeftIsAZeroCount()
    {
        var robot = new Recorder("""{"msgId":"1","code":0,"data":{"map_id":1000000002,"map_type":0,"timestamp":0}}""");

        await robot.SetRestrictionsAsync(MapId, []);

        AssertJson("""{"virwall":[0]}""", robot.Sent[0].Payload);
    }

    [Fact]
    public async Task FurnitureGoesOutAsAJsonStringWithItsIndexCodeAndCorners()
    {
        // The adjust_furniture of the 23 September capture: stove, washing machine, fridge.
        var robot = new Recorder("""{"msgId":"1","code":0,"method":"service.adjust_furniture","data":{"map_id":0,"map_type":0,"timestamp":1790194581,"package":[1,1]}}""");
        FurniturePiece[] pieces =
        [
            new(1, FurnitureKind.FromCode(1608)!, [new(-2.646453857421875, -0.1718042492866516), new(-2.646453857421875, -0.7718043923377991),
                                                   new(-2.0464539527893066, -0.7718043923377991), new(-2.0464539527893066, -0.1718042492866516)]),
            new(2, FurnitureKind.FromCode(1524)!, [new(-1.9663420915603638, 0.0014677643775939941), new(-1.9663420915603638, -0.9985321164131165),
                                                   new(-1.0663419961929321, -0.9985321164131165), new(-1.0663419961929321, 0.0014677643775939941)]),
            new(3, FurnitureKind.FromCode(1516)!, [new(-1.0301086902618408, -0.10653859376907349), new(-1.0301086902618408, -0.9065385460853577),
                                                   new(-0.23010873794555664, -0.9065385460853577), new(-0.23010873794555664, -0.10653859376907349)]),
        ];

        var result = await robot.AdjustFurnitureAsync(pieces, DateTimeOffset.FromUnixTimeSeconds(1790194580));

        Assert.NotNull(result);   // map_id 0 is the robot's normal answer here
        var sent = (JsonObject)robot.Sent.Single().Payload;
        Assert.Equal(1790194580, sent["timestamp"]!.GetValue<long>());
        AssertJson("[1,1]", sent["package"]!);
        // A string holding JSON, not an array.
        var list = sent["furniture_list"]!.GetValue<string>();
        AssertJson("""
            [[1,1608,1,-2.646453857421875,-0.1718042492866516,-2.646453857421875,-0.7718043923377991,-2.0464539527893066,-0.7718043923377991,-2.0464539527893066,-0.1718042492866516],
             [2,1524,1,-1.9663420915603638,0.0014677643775939941,-1.9663420915603638,-0.9985321164131165,-1.0663419961929321,-0.9985321164131165,-1.0663419961929321,0.0014677643775939941],
             [3,1516,1,-1.0301086902618408,-0.10653859376907349,-1.0301086902618408,-0.9065385460853577,-0.23010873794555664,-0.9065385460853577,-0.23010873794555664,-0.10653859376907349]]
            """, JsonNode.Parse(list)!);
    }

    [Fact]
    public async Task NoFurnitureLeftIsAnEmptyListString()
    {
        var robot = new Recorder("""{"msgId":"1","code":0,"data":{"map_id":0,"map_type":0,"timestamp":1,"package":[1,1]}}""");

        await robot.AdjustFurnitureAsync([]);

        Assert.Equal("[]", robot.Sent[0].Payload["furniture_list"]!.GetValue<string>());
    }

    [Fact]
    public async Task AShapeWithoutFourCornersIsRefusedBeforeAnythingIsSent()
    {
        var robot = new Recorder("{}");

        await Assert.ThrowsAsync<ArgumentException>(() =>
            robot.SetRestrictionsAsync(MapId, [new(RestrictionKind.All[0], [new(0, 0), new(1, 1)])]));
        Assert.Empty(robot.Sent);
    }

    // ---- Catalogues ------------------------------------------------------------------------

    [Fact]
    public void TheFourZoneKindsPairJdmTypesWithRestBehaviours()
    {
        Assert.Equal([2, 13, 12, 6], RestrictionKind.All.Select(k => k.JdmType));
        Assert.Equal("Lavage uniquement", RestrictionKind.FromBehavior("brushBarOff")!.Label);
        Assert.Null(RestrictionKind.FromBehavior("brushBarAndTraction"));   // named in the app, never seen
    }

    [Fact]
    public void TheTwentyFourPiecesOfFurnitureHaveDistinctCodesAndTypes()
    {
        Assert.Equal(24, FurnitureKind.All.Count);
        Assert.Equal(24, FurnitureKind.All.Select(k => k.Code).Distinct().Count());
        Assert.Equal(24, FurnitureKind.All.Select(k => k.RestType).Distinct().Count());
        Assert.Equal(1524, FurnitureKind.FromRestType("washingMachine")!.Code);
        Assert.Null(FurnitureKind.FromCode(1517));   // a gap in the numbering: never sent
    }

    // ---- Geometry ------------------------------------------------------------------------------

    [Fact]
    public void ARectangleListsItsCornersTheWayThePhoneDoes()
    {
        // Top-left, bottom-left, bottom-right, top-right, whichever two corners were clicked.
        Assert.Equal([new(1, 3), new(1, 1), new(4, 1), new(4, 3)], MapShapes.Rectangle(new(4, 1), new(1, 3)));
    }

    [Fact]
    public void ANewPieceIsCentredWithItsLengthUpright()
    {
        var corners = MapShapes.Centred(new(0, 0), length: 2, width: 1);

        Assert.Equal([new(-0.5, 1), new(-0.5, -1), new(0.5, -1), new(0.5, 1)], corners);
        Assert.Equal((2.0, 1.0), MapShapes.Sides(corners));
    }

    [Fact]
    public void AQuarterTurnIsClockwiseAboutTheCentreAndSwapsTheSides()
    {
        var turned = MapShapes.RotateClockwise(MapShapes.Centred(new(10, 5), length: 2, width: 1));

        // The top-left corner goes to the top-right, and so on round.
        Assert.Equal([new(11, 5.5), new(9, 5.5), new(9, 4.5), new(11, 4.5)], turned);
        Assert.Equal((2.0, 1.0), MapShapes.Sides(turned));
        Assert.Equal(new Point(10, 5), MapShapes.Centre(turned));
    }

    [Fact]
    public void PointsAreFoundInsideShapesAndAreasAreMeasured()
    {
        var square = MapShapes.Rectangle(new(0, 0), new(2, 2));

        Assert.True(MapShapes.Contains(square, 1, 1));
        Assert.False(MapShapes.Contains(square, 3, 1));
        Assert.Equal(4, MapShapes.Area(square), 6);
        Assert.Equal([new(1, 2), new(1, 0), new(3, 0), new(3, 2)], MapShapes.Translate(square, 1, 0));
    }

    [Fact]
    public void APiecesOrientationIsCountedInClockwiseQuarterTurnsFromHowANewOneIsLaidOut()
    {
        var laidOut = MapShapes.Centred(new(3, 3), length: 2, width: 1);

        Assert.Equal(0, MapShapes.QuarterTurns(laidOut));
        Assert.Equal(1, MapShapes.QuarterTurns(MapShapes.RotateClockwise(laidOut)));
        Assert.Equal(3, MapShapes.QuarterTurns(MapShapes.Oriented(new(3, 3), 2, 1, 3)));
        Assert.Equal(0, MapShapes.QuarterTurns(MapShapes.Oriented(new(3, 3), 2, 1, 4)));
        // The captured two-seater sofa: its first side runs right to left, a quarter turn.
        Assert.Equal(1, MapShapes.QuarterTurns([new(5.68, 0), new(4.58, 0), new(4.58, -1.8), new(5.68, -1.8)]));
        // At an angle no quarter turn gives.
        Assert.Null(MapShapes.QuarterTurns([new(0, 0), new(1, -1), new(2, 0), new(1, 1)]));
    }

    [Fact]
    public void TurningAPieceKeepsItsCentreAndItsSides()
    {
        var turned = MapShapes.Oriented(new(10, 5), 2.1, 1.8, 2);

        Assert.Equal(new Point(10, 5), MapShapes.Centre(turned));
        var (first, second) = MapShapes.Sides(turned);
        Assert.Equal(2.1, first, 9);
        Assert.Equal(1.8, second, 9);
    }

    private static void AssertJson(string expected, JsonNode actual)
    {
        var expectedNode = JsonNode.Parse(expected);
        Assert.True(JsonNode.DeepEquals(expectedNode, actual), $"expected {expectedNode!.ToJsonString()}\n but got {actual.ToJsonString()}");
    }

    private sealed class Recorder(string reply) : IRobotCommands
    {
        public List<(string Method, JsonNode Payload)> Sent { get; } = [];

        public Task<JsonObject> RequestJdmAsync(string method, JsonObject? parameters = null, TimeSpan? timeout = null, CancellationToken ct = default)
        {
            Sent.Add((method, parameters?.DeepClone() ?? new JsonObject()));
            return Task.FromResult((JsonObject)JsonNode.Parse(reply)!);
        }

        public Task PublishJdmAsync(string method, JsonObject? parameters = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task PublishCommandAsync(JsonObject payload, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
