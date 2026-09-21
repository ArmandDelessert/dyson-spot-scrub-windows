using System.Text.Json.Nodes;
using MyDyson.Core;

namespace MyDyson.Core.Tests;

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
        RoomSelection[] rooms = [new("12", CleanType.Vacuum, Order: 2), new("10", CleanType.VacuumThenMop, Order: 1)];

        await CleaningSequence.StartAsync(robot, MapId, rooms);

        Assert.Equal(
            ["service.get_preference", "service.set_preference", "START", "service.set_cur_map", "service.set_room_clean"],
            robot.Sent.Select(m => m.Method));
        AssertJson("""{"map_id":1000000002}""", robot.Sent[0].Payload);
        // Only indices 3 (clean type), 8 (selected) and 10 (order) change; the twelfth element is
        // dropped because the app publishes eleven. Couloir keeps its type and order but is
        // deselected, Pièce1 stays all zeros.
        AssertJson("""
            {"map_id":1000000002,"prefer_type":1,
             "room_preference":[[12,"Chambre",0,0,0,0,0,0,1,0,2],
                                [13,"Couloir",0,1,0,0,0,0,0,0,2],
                                [10,"Cuisine",0,3,0,1,1,0,1,0,1],
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

        await CleaningSequence.StartAsync(robot, MapId, [new("14", CleanType.Mop, Order: 1)]);

        AssertJson("""
            {"map_id":1000000002,"prefer_type":1,
             "room_preference":[[11,"Salle de bain",0,1,0,2,1,0,0,0,3],[14,"Salon",0,2,0,0,0,0,1,0,1]],
             "uv_switch":[[11,1],[14,0]]}
            """, robot.Sent[1].Payload);
    }

    [Fact]
    public async Task RefusesAnEmptySelectionBeforeTalkingToTheRobot()
    {
        var robot = new RecordingRobot(PreferenceReply);
        await Assert.ThrowsAsync<ArgumentException>(() => CleaningSequence.StartAsync(robot, MapId, []));
        Assert.Empty(robot.Sent);
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
