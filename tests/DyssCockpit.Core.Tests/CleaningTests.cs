using System.Text.Json.Nodes;
using DyssCockpit.Core;

namespace DyssCockpit.Core.Tests;

public class CleaningTests
{
    [Theory]
    [InlineData(CleanType.Vacuum, "vacuum", 0)]
    [InlineData(CleanType.VacuumAndMop, "vacuumAndMop", 1)]
    [InlineData(CleanType.Mop, "mop", 2)]
    [InlineData(CleanType.VacuumThenMop, "vacuumThenMop", 3)]
    public void RestAndJdmMappingsRoundTrip(CleanType type, string rest, int jdm)
    {
        Assert.Equal(rest, type.ToRest());
        Assert.Equal(type, CleanTypes.FromRest(rest));
        Assert.Equal(jdm, type.ToJdm());
        Assert.Equal(type, CleanTypes.FromJdm(jdm));
    }

    [Fact]
    public void UnknownRestValueFallsBackToVacuum()
    {
        Assert.Equal(CleanType.Vacuum, CleanTypes.FromRest(null));
        Assert.Equal(CleanType.Vacuum, CleanTypes.FromRest("something-new"));
    }
}

public class CleaningSequenceTests
{
    private const long MapId = 1000000002;

    // Anonymised service.get_preference reply captured on 2026-09-20: twelve elements per room,
    // no uv_switch. Cuisine already set to vacuum-and-mop with extra flags at 5 and 6, Pièce1 a
    // never-selected lidar artefact.
    private const string PreferenceReply = """
        {"msgId":"1","code":0,"method":"service.get_preference","data":{
          "room":[[12,"Chambre",0,1,0,0,0,0,1,0,1,0],
                  [13,"Couloir",0,1,0,0,0,0,1,0,2,0],
                  [10,"Cuisine",0,1,0,1,1,0,1,0,4,0],
                  [15,"Pièce1",0,0,0,0,0,0,0,0,0,0]],
          "material":[],"prefer_on":1}}
        """;

    [Fact]
    public async Task PublishesTheFourMessagesOfTheOfficialAppInOrder()
    {
        var robot = new RecordingRobot(PreferenceReply);
        RoomSelection[] rooms =
        [
            new("12", new RoomSettings(CleanType.Vacuum), Order: 2),
            new("10", new RoomSettings(CleanType.VacuumThenMop, CleaningStrategy.Quiet, WaterLevel.High, MopPasses: 2), Order: 1),
        ];

        await CleaningSequence.StartAsync(robot, MapId, rooms, TestContext.Current.CancellationToken);

        Assert.Equal(
            ["service.get_preference", "service.set_preference", "START", "service.set_cur_map", "service.set_room_clean"],
            robot.Sent.Select(m => m.Method));
        AssertJson("""{"map_id":1000000002}""", robot.Sent[0].Payload);
        // The chosen rooms get their settings (indices 3 to 6), 8 (selected) and 10 (order); the
        // twelfth element is dropped because the app publishes eleven. Couloir keeps its type and
        // order but is deselected, Pièce1 stays all zeros.
        AssertJson("""
            {"map_id":1000000002,"prefer_type":1,
             "room_preference":[[12,"Chambre",0,0,0,0,0,0,1,0,2],
                                [13,"Couloir",0,1,0,0,0,0,0,0,2],
                                [10,"Cuisine",0,3,2,2,1,0,1,0,1],
                                [15,"Pièce1",0,0,0,0,0,0,0,0,0]],
             "uv_switch":[]}
            """, robot.Sent[1].Payload);
        // Zones in cleaning order, as strings on the classic topic and numbers on the jdm one.
        AssertJson("""
            {"msg":"START","mode-reason":"RAPP","cleaningMode":"zoneConfigured","fullCleanType":"immediate",
             "cleaningProgramme":{"persistentMapId":"1000000002","zonesDefinitionLastUpdatedDate":"","unorderedZones":["10","12"]}}
            """, robot.Sent[2].Payload);
        AssertJson("""{"map_id":1000000002}""", robot.Sent[3].Payload);
        AssertJson("""{"ctrl_value":1,"clean_type":0,"room_ids":[10,12]}""", robot.Sent[4].Payload);
    }

    [Fact]
    public async Task ForwardsUvSwitchAndToleratesShortOrMalformedEntries()
    {
        // Eleven-element entries are used as is, shorter ones padded, junk skipped.
        var robot = new RecordingRobot("""
            {"msgId":"1","code":0,"method":"service.get_preference","data":{
              "room":[[11,"Salle de bain",0,1,0,2,1,0,1,0,3],[14,"Salon"],[9],"x",null],
              "uv_switch":[[11,1],[14,0]]}}
            """);

        await CleaningSequence.StartAsync(robot, MapId, [new("14", new RoomSettings(CleanType.Mop), Order: 1)], TestContext.Current.CancellationToken);

        AssertJson("""
            {"map_id":1000000002,"prefer_type":1,
             "room_preference":[[11,"Salle de bain",0,1,0,2,1,0,0,0,3],[14,"Salon",0,2,0,0,0,0,1,0,1]],
             "uv_switch":[[11,1],[14,0]]}
            """, robot.Sent[1].Payload);
    }

    [Fact]
    public async Task ARoomIdSentAsTextOrAsALargeNumberIsStillRecognised()
    {
        var robot = new RecordingRobot("""
            {"msgId":"1","code":0,"method":"service.get_preference","data":{
              "room":[["11","Salle de bain",0,0,0,0,0,0,0,0,1],[3000000000,"Salon",0,0,0,0,0,0,0,0,2]]}}
            """);

        await CleaningSequence.StartAsync(robot, MapId, [new("11", new RoomSettings(CleanType.Mop), Order: 1)], TestContext.Current.CancellationToken);

        AssertJson("""
            {"map_id":1000000002,"prefer_type":1,
             "room_preference":[["11","Salle de bain",0,2,0,0,0,0,1,0,1],[3000000000,"Salon",0,0,0,0,0,0,0,0,2]],
             "uv_switch":[]}
            """, robot.Sent[1].Payload);
    }

    [Fact]
    public async Task RefusesAnEmptySelectionBeforeTalkingToTheRobot()
    {
        var robot = new RecordingRobot(PreferenceReply);
        await Assert.ThrowsAsync<ArgumentException>(() => CleaningSequence.StartAsync(robot, MapId, [], TestContext.Current.CancellationToken));
        Assert.Empty(robot.Sent);
    }

    [Fact]
    public async Task AZoneCleanSendsTheThreeMessagesOfThePhone()
    {
        // Captured on 2026-09-30: a rectangle drawn on the phone, vacuum in quiet mode.
        var robot = new RecordingRobot("{}");
        var corners = SpotCleanSequence.Corners(new(0.440765380859375, 0.28673648834228516), new(1.8325986862182617, 2.6739816665649414));

        await SpotCleanSequence.StartAsync(robot, MapId, corners, new RoomSettings(CleanType.Vacuum, CleaningStrategy.Quiet), "40328115-26bc-4e66-bf76-5fee0bf2a49c", TestContext.Current.CancellationToken);

        Assert.Equal(["START", "service.set_cur_map", "service.set_areas_start"], robot.Sent.Select(m => m.Method));
        AssertJson("""
            {"cleaningProgramme":{"persistentMapId":"1000000002","spotZones":[{"id":"40328115-26bc-4e66-bf76-5fee0bf2a49c","points":[
               {"x":1.8325986862182617,"y":2.6739816665649414},{"x":0.440765380859375,"y":2.6739816665649414},
               {"x":0.440765380859375,"y":0.28673648834228516},{"x":1.8325986862182617,"y":0.28673648834228516}]}],
             "defaultSpotZoneSettings":{"cleaningStrategy":"quiet","cleanType":"vacuum","waterLevel":"low","mopPasses":1,"dryPasses":1}},
             "cleaningMode":"spotZoneConfigured","fullCleanType":"immediate","mode-reason":"RAPP","msg":"START"}
            """, robot.Sent[0].Payload);
        AssertJson("""{"map_id":1000000002}""", robot.Sent[1].Payload);
        AssertJson("""
            {"ctrl_value":1,"zone_points":[[1.8325986862182617,2.6739816665649414,0.440765380859375,2.6739816665649414,0.440765380859375,0.28673648834228516,1.8325986862182617,0.28673648834228516]],
             "mode":0,"wind":2,"water":0,"clean_count":0,"dry_clean_count":0,"action":0,"uv_switch":0}
            """, robot.Sent[2].Payload);
    }

    [Fact]
    public async Task AMoppedZoneCarriesItsWaterAndPasses()
    {
        var robot = new RecordingRobot("{}");

        await SpotCleanSequence.StartAsync(robot, MapId, SpotCleanSequence.Corners(new(0, 0), new(1, 1)),
            new RoomSettings(CleanType.VacuumThenMop, CleaningStrategy.Boost, WaterLevel.High, 2), ct: TestContext.Current.CancellationToken);

        var jdm = robot.Sent[2].Payload;
        Assert.Equal((3, 1, 2, 1), (jdm["mode"]!.GetValue<int>(), jdm["wind"]!.GetValue<int>(), jdm["water"]!.GetValue<int>(), jdm["clean_count"]!.GetValue<int>()));
        Assert.Equal("vacuumThenMop", robot.Sent[0].Payload["cleaningProgramme"]!["defaultSpotZoneSettings"]!["cleanType"]!.GetValue<string>());
    }

    private static void AssertJson(string expected, JsonNode actual)
    {
        var expectedNode = JsonNode.Parse(expected);
        Assert.True(JsonNode.DeepEquals(expectedNode, actual), $"expected {expectedNode!.ToJsonString()}\n but got {actual.ToJsonString()}");
    }

    /// <summary>Answers every request with the same reply and keeps what was published.</summary>
    private sealed class RecordingRobot(string reply) : IRobotCommands
    {
        public List<(string Method, JsonNode Payload)> Sent { get; } = [];

        public Task<JsonObject> RequestJdmAsync(string method, JsonObject? parameters = null, TimeSpan? timeout = null, CancellationToken ct = default)
        {
            Sent.Add((method, parameters?.DeepClone() ?? new JsonObject()));
            return Task.FromResult((JsonObject)JsonNode.Parse(reply)!);
        }

        public Task PublishJdmAsync(string method, JsonObject? parameters = null, CancellationToken ct = default)
        {
            Sent.Add((method, parameters?.DeepClone() ?? new JsonObject()));
            return Task.CompletedTask;
        }

        public Task PublishCommandAsync(JsonObject payload, CancellationToken ct = default)
        {
            Sent.Add((payload["msg"]!.GetValue<string>(), payload.DeepClone()));
            return Task.CompletedTask;
        }
    }
}

/// <summary>
/// The map-editing calls, checked against the payloads captured on 2026-09-19 while the official
/// app renamed, merged and split rooms of a real map.
/// </summary>
public class MapEditingCommandTests
{
    private const long MapId = 1000000002;

    private static RecordingRobot Robot() =>
        new("""{"msgId":"1","code":0,"data":{"map_id":1000000002,"map_type":3,"timestamp":1789835979}}""");

    [Fact]
    public async Task RenamingAMapSendsAPlainName()
    {
        var robot = Robot();

        var result = await robot.RenameMapAsync(MapId, "Appartement Rez v2", TestContext.Current.CancellationToken);

        var (method, payload) = Assert.Single(robot.Sent);
        Assert.Equal("service.rename_map", method);
        AssertJson("""{"map_id":1000000002,"map_name":"Appartement Rez v2"}""", payload);
        Assert.Equal(new MapEditResult(MapId, 3, 1789835979), result);
    }

    [Fact]
    public async Task RenamingARoomSendsTheTypeAndTheNameTogether()
    {
        var robot = Robot();

        await robot.RenameRoomAsync(MapId, 15, "Débarras", "storageRoom", TestContext.Current.CancellationToken);
        await robot.RenameRoomAsync(MapId, 16, "Pièce secrète", ct: TestContext.Current.CancellationToken);

        // Non-ASCII stays \u-escaped inside the nested name, byte for byte what the capture shows.
        Assert.Equal(["service.rename_room", "service.rename_room"], robot.Sent.Select(m => m.Method));
        AssertJson("""{"map_id":1000000002,"room_id":15,"room_name":"{\"type\":\"storageRoom\",\"name\":\"D\\u00E9barras\"}"}""", robot.Sent[0].Payload);
        // No type given means a name the user made up, which the app sends as type "custom".
        AssertJson("""{"map_id":1000000002,"room_id":16,"room_name":"{\"type\":\"custom\",\"name\":\"Pi\\u00E8ce secr\\u00E8te\"}"}""", robot.Sent[1].Payload);
    }

    [Fact]
    public async Task MergingAndSplittingCarryTheLanguageTheAppSends()
    {
        var robot = Robot();

        await robot.MergeRoomsAsync(MapId, [16, 15], ct: TestContext.Current.CancellationToken);
        await robot.SplitRoomAsync(MapId, 10, new Point(-0.825, -1.821), new Point(3.8, -1.821), ct: TestContext.Current.CancellationToken);

        AssertJson("""{"map_id":1000000002,"room_ids":[16,15],"lang":5}""", robot.Sent[0].Payload);
        AssertJson("""{"map_id":1000000002,"room_id":10,"split_points":[-0.825,-1.821,3.8,-1.821],"lang":5}""", robot.Sent[1].Payload);
    }

    [Fact]
    public async Task DeletingAMapSendsItsIdAndReadsTheDuplicatedResultKey()
    {
        // Captured 2026-09-23: the reply really carries "result" twice. It must still read as a
        // success rather than blow up on the duplicate key.
        var robot = new RecordingRobot("""{"msgId":"1","code":0,"method":"service.del_map","data":{"result":0,"result":0}}""");

        var result = await robot.DeleteMapAsync(MapId, TestContext.Current.CancellationToken);

        var (method, payload) = Assert.Single(robot.Sent);
        Assert.Equal("service.del_map", method);
        AssertJson("""{"map_id":1000000002}""", payload);
        Assert.NotNull(result);
    }

    [Fact]
    public void ARefusedSplitIsReadAsRefused()
    {
        // Captured 2026-09-23: a cut the robot would not make answers {"result": 1}, not the map
        // shape a successful split answers with.
        Assert.Null(MapEditResult.From((JsonObject)JsonNode.Parse(
            """{"msgId":"1","code":0,"method":"service.split_room","data":{"result":1}}""")!));
    }

    [Fact]
    public async Task ARefusedEditComesBackAsNothingRatherThanAnError()
    {
        // The robot answers a refused edit with some other shape, never an error field of its own.
        var robot = new RecordingRobot("""{"msgId":"1","code":1,"data":{"result":1}}""");

        Assert.Null(await robot.RenameMapAsync(MapId, "Peu importe", TestContext.Current.CancellationToken));
    }

    private static void AssertJson(string expected, JsonNode actual)
    {
        var expectedNode = JsonNode.Parse(expected);
        Assert.True(JsonNode.DeepEquals(expectedNode, actual), $"expected {expectedNode!.ToJsonString()}\n but got {actual.ToJsonString()}");
    }

    /// <summary>Same idea as CleaningSequenceTests's own recorder, kept separate so each reads on its own.</summary>
    private sealed class RecordingRobot(string reply) : IRobotCommands
    {
        public List<(string Method, JsonNode Payload)> Sent { get; } = [];

        public Task<JsonObject> RequestJdmAsync(string method, JsonObject? parameters = null, TimeSpan? timeout = null, CancellationToken ct = default)
        {
            Sent.Add((method, parameters?.DeepClone() ?? new JsonObject()));
            return Task.FromResult((JsonObject)JsonNode.Parse(reply)!);
        }

        public Task PublishJdmAsync(string method, JsonObject? parameters = null, CancellationToken ct = default) =>
            throw new InvalidOperationException("map editing waits for the robot's reply");

        public Task PublishCommandAsync(JsonObject payload, CancellationToken ct = default) =>
            throw new InvalidOperationException("map editing is jdm only");
    }
}
