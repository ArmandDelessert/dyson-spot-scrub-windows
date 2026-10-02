using System.Text.Json;
using System.Text.Json.Nodes;
using Dyss.Core;

namespace Dyss.Core.Tests;

/// <summary>
/// Schedules, checked against the add_order payloads the phone app sent on 2026-09-25 and 26 (map
/// ids anonymised). Each was set up on purpose: several days, every clean type, every vacuum power,
/// water level and pass count, rooms left out.
/// </summary>
public class ScheduleTests
{
    // Lundi, jeudi, dimanche à 12:30 on a six-room map; three rooms cleaned, one of each clean type
    // but vacuum-and-mop, three left out.
    private const string ThreeRoomsThreeDays = """
        {"id":250943370,"enable":1,"day":73,"hour":12,"minute":30,"repeat":1,"map_id":1000000002,"room_count":6,
         "time_zone":3600,"prefer_type":1,"is_global":0,"areas":[],
         "room_preference":[[11,"Pièce1",0,0,0,0,0,0,1,0,0,0],[15,"Pièce3",0,2,0,0,0,0,1,0,1,0],[13,"Pièce2",0,3,0,0,0,0,1,0,2,0],
                            [12,"Pièce1",0,0,0,0,0,0,0,0,3,0],[10,"Pièce1",0,0,0,0,0,0,0,0,4,0],[14,"Pièce4",0,0,0,0,0,0,0,0,5,0]],
         "uv_switch":[[11,1],[15,0],[13,0],[12,1],[10,1],[14,1]]}
        """;

    // Every day at 10:00: Silencieux, Élevé, 1 x and Rapide, Moyen, 2 x.
    private const string EveryDay = """
        {"id":595016193,"enable":1,"day":127,"hour":10,"minute":0,"repeat":1,"map_id":1000000003,"room_count":2,
         "time_zone":3600,"prefer_type":1,"is_global":0,"areas":[],
         "room_preference":[[10,"Null",0,1,2,2,0,0,1,0,0,0],[11,"Bureau",0,1,3,1,1,0,1,0,1,0]],
         "uv_switch":[[10,0],[11,0]]}
        """;

    // Wednesday and Friday at 12:00: Lavez, Élevé, 2 x, then Aspirez puis lavez, Boost, Faible, 1 x.
    private const string WednesdayAndFriday = """
        {"id":880610700,"enable":1,"day":20,"hour":12,"minute":0,"repeat":1,"map_id":1000000003,"room_count":2,
         "time_zone":3600,"prefer_type":1,"is_global":0,"areas":[],
         "room_preference":[[11,"Bureau",0,2,0,2,1,0,1,0,0,0],[10,"Null",0,3,1,0,0,0,1,0,1,0]],
         "uv_switch":[[11,0],[10,0]]}
        """;

    private static readonly MapRoom[] SixRooms =
        [new(11, "Pièce1"), new(15, "Pièce3"), new(13, "Pièce2"), new(12, "Pièce1"), new(10, "Pièce1"), new(14, "Pièce4")];
    private static readonly MapRoom[] TwoRooms = [new(10, "Null"), new(11, "Bureau")];

    [Theory]
    [InlineData(ThreeRoomsThreeDays, "six")]
    [InlineData(EveryDay, "two")]
    [InlineData(WednesdayAndFriday, "two")]
    public void ReadingACapturedScheduleAndWritingItBackGivesTheSamePayload(string captured, string map)
    {
        var p = (JsonObject)JsonNode.Parse(captured)!;

        var schedule = CleaningSchedule.FromParams(p)!;

        AssertJson(captured, schedule.ToParams(map == "six" ? SixRooms : TwoRooms, 3600));
    }

    [Fact]
    public void DaysAreOneBitEachMondayFirst()
    {
        var s = CleaningSchedule.FromParams((JsonObject)JsonNode.Parse(ThreeRoomsThreeDays)!)!;

        Assert.Equal(ScheduleDays.Monday | ScheduleDays.Thursday | ScheduleDays.Sunday, s.Days);
        Assert.Equal((12, 30), (s.Hour, s.Minute));
        Assert.Equal([11, 15, 13], s.Rooms.Select(r => r.ZoneId));
        Assert.Equal([CleanType.Vacuum, CleanType.Mop, CleanType.VacuumThenMop], s.Rooms.Select(r => r.Settings.CleanType));
    }

    [Fact]
    public void EveryRoomSettingIsReadFromItsOwnIndex()
    {
        var s = CleaningSchedule.FromParams((JsonObject)JsonNode.Parse(EveryDay)!)!;

        Assert.Equal(new RoomSettings(CleanType.VacuumAndMop, CleaningStrategy.Quiet, WaterLevel.High, 1), s.Rooms[0].Settings);
        Assert.Equal(new RoomSettings(CleanType.VacuumAndMop, CleaningStrategy.Quick, WaterLevel.Medium, 2), s.Rooms[1].Settings);
    }

    [Fact]
    public void SettingsThatDoNotApplyToTheCleanTypeGoOutAsZero()
    {
        // A vacuum power on a mop-only room, a water level on a vacuum-only one: the phone sends 0.
        var s = new CleaningSchedule(1, 1000000003, true, ScheduleDays.Monday, 8, 0,
        [
            new(10, new RoomSettings(CleanType.Mop, CleaningStrategy.Boost, WaterLevel.High, 2)),
            new(11, new RoomSettings(CleanType.Vacuum, CleaningStrategy.Quick, WaterLevel.High, 2)),
        ]);

        var rooms = (JsonArray)s.ToParams(TwoRooms, 3600)["room_preference"]!;

        AssertJson("""[10,"Null",0,2,0,2,1,0,1,0,0,0]""", rooms[0]!);
        AssertJson("""[11,"Bureau",0,0,3,0,0,0,1,0,1,0]""", rooms[1]!);
    }

    [Fact]
    public void RoomsTheMapNoLongerHasAreLeftOutAndTheCountFollows()
    {
        var s = new CleaningSchedule(7, 1000000003, false, ScheduleDays.Weekend, 9, 5,
            [new(99, new RoomSettings(CleanType.Vacuum)), new(11, new RoomSettings(CleanType.Vacuum))]);

        var p = s.ToParams(TwoRooms, 7200);

        Assert.Equal(2, p["room_count"]!.GetValue<int>());
        Assert.Equal(0, p["enable"]!.GetValue<int>());
        Assert.Equal(96, p["day"]!.GetValue<int>());
        Assert.Equal(7200, p["time_zone"]!.GetValue<int>());
        AssertJson("""[[11,"Bureau",0,0,0,0,0,0,1,0,0,0],[10,"Null",0,0,0,0,0,0,0,0,1,0]]""", p["room_preference"]!);
    }

    [Fact]
    public async Task SavingSendsAddOrderAndDeletingDelOrder()
    {
        var robot = new Recorder("""{"msgId":"1","code":0,"method":"service.add_order","data":{"result":0}}""");
        var s = CleaningSchedule.FromParams((JsonObject)JsonNode.Parse(EveryDay)!)!;

        Assert.True(await robot.SaveScheduleAsync(s, TwoRooms, 3600));
        Assert.True(await robot.DeleteScheduleAsync(s.Id));

        Assert.Equal(["service.add_order", "service.del_order"], robot.Sent.Select(m => m.Method));
        AssertJson(EveryDay, robot.Sent[0].Payload);
        AssertJson("""{"id":595016193}""", robot.Sent[1].Payload);
    }

    [Fact]
    public async Task ARefusalIsReportedAsFalse()
    {
        var robot = new Recorder("""{"msgId":"1","code":1,"data":{"result":1}}""");

        Assert.False(await robot.DeleteScheduleAsync(42));
    }

    [Fact]
    public async Task TheSummaryIsAllGetOrderGives()
    {
        // Captured on 2026-09-25 with three schedules on the active map, two of them on.
        var robot = new Recorder("""
            {"msgId":"1","code":0,"method":"service.get_order","data":{"order_data_lite":
             {"total":3,"enable":2,"timestamp":1790372729,"md5":"0added44d13eee9d0a2a1332a8332fcf"}}}
            """);

        var summary = await robot.GetScheduleSummaryAsync();

        Assert.Equal(new ScheduleSummary(3, 2, "0added44d13eee9d0a2a1332a8332fcf"), summary);
        AssertJson("{}", robot.Sent.Single().Payload);
    }

    [Fact]
    public void TheCountPushedAfterEachChangeLandsInTheJdmProperties()
    {
        var jdm = new JdmProperties();
        jdm.Merge((JsonObject)JsonNode.Parse("""{"order_total":{"total":2,"enable":1}}""")!);

        Assert.Equal(new ScheduleSummary(2, 1, null), jdm.OrderTotal);
    }

    [Fact]
    public void TheTimeZoneFieldIsTheRobotZonesOffsetAtThatMoment()
    {
        // What the phone sent in September for Europe/London, the zone the cloud holds.
        Assert.Equal(3600, CleaningSchedule.TimeZoneOffsetSeconds("Europe/London", new DateTimeOffset(2026, 9, 25, 12, 0, 0, TimeSpan.Zero)));
        Assert.Equal(0, CleaningSchedule.TimeZoneOffsetSeconds("Europe/London", new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero)));
        Assert.Null(CleaningSchedule.TimeZoneOffsetSeconds("Nowhere/Atlantis", DateTimeOffset.UtcNow));
        Assert.Null(CleaningSchedule.TimeZoneOffsetSeconds(null, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void NewIdsArePositive32BitNumbers()
    {
        for (var i = 0; i < 100; i++) Assert.InRange(CleaningSchedule.NewId(), 1, int.MaxValue);
    }

    // What the cloud's scheduler answered on 2026-09-28 for two schedules made on the phone
    // (serial anonymised, rooms trimmed to three).
    internal const string CloudEvents = """
        {"enabled":true,"serial":"SERIAL","events":[
          {"groupId":11634012,"days":[1],"startTime":"10:00","weeklyRepeat":true,"enabled":false,
           "settings":{"persistentMapId":"1000000002","zones":[
             {"id":"12","name":"Chambre","type":"","isSelected":true,"order":0,
              "settings":{"vacuumPowerMode":0,"cleaningStrategy":"auto","cleanType":"vacuumAndMop","waterLevel":"low","mopPasses":1,"dryPasses":1,"isUvScanOn":false}},
             {"id":"10","name":"Cuisine","type":"","isSelected":false,"order":1,
              "settings":{"vacuumPowerMode":0,"cleaningStrategy":"auto","cleanType":"vacuum","waterLevel":"low","mopPasses":1,"dryPasses":1,"isUvScanOn":false}}]}},
          {"groupId":544623979,"days":[0],"startTime":"11:00","weeklyRepeat":true,"enabled":true,
           "settings":{"persistentMapId":"1000000002","zones":[
             {"id":"11","name":"Salle de bain","type":"","isSelected":true,"order":0,
              "settings":{"cleaningStrategy":"boost","cleanType":"vacuumThenMop","waterLevel":"high","mopPasses":2}},
             {"id":"12","name":"Chambre","type":"","isSelected":true,"order":1,
              "settings":{"cleaningStrategy":"auto","cleanType":"vacuum","waterLevel":"low","mopPasses":1}}]}}]}
        """;

    [Fact]
    public void TheCloudsEventsReadAsSchedulesWithSundayAsDayZero()
    {
        var events = JsonSerializer.Deserialize<ScheduleEvents>(CloudEvents)!;

        var monday = events.Events![0].ToSchedule()!;
        Assert.Equal(new CleaningSchedule(11634012, 1000000002, false, ScheduleDays.Monday, 10, 0,
            [new(12, new RoomSettings(CleanType.VacuumAndMop))]) with { Rooms = [] }, monday with { Rooms = [] });
        Assert.Equal([12], monday.Rooms.Select(r => r.ZoneId));

        var sunday = events.Events[1].ToSchedule()!;
        Assert.Equal((ScheduleDays.Sunday, 11, 0, true), (sunday.Days, sunday.Hour, sunday.Minute, sunday.Enabled));
        // Selected rooms only, in the event's order, with their settings.
        Assert.Equal([11, 12], sunday.Rooms.Select(r => r.ZoneId));
        Assert.Equal(new RoomSettings(CleanType.VacuumThenMop, CleaningStrategy.Boost, WaterLevel.High, 2), sunday.Rooms[0].Settings);
    }

    [Theory]
    [InlineData(0, ScheduleDays.Sunday)]
    [InlineData(1, ScheduleDays.Monday)]
    [InlineData(4, ScheduleDays.Thursday)]
    [InlineData(6, ScheduleDays.Saturday)]
    [InlineData(7, ScheduleDays.None)]
    public void CloudDaysCountFromSunday(int day, ScheduleDays expected) =>
        Assert.Equal(expected, ScheduleEvent.DayFromCloud(day));

    [Fact]
    public void AnEventWithoutItsMapOrTimeIsLeftOut()
    {
        Assert.Null(new ScheduleEvent(1, [1], "10:00", true, true, null).ToSchedule());
        Assert.Null(new ScheduleEvent(1, [1], "dix heures", true, true, new ScheduleEventSettings("1000000002", [])).ToSchedule());
    }

    // ---- Days and timing -------------------------------------------------------------

    [Theory]
    [InlineData(ScheduleDays.All, "tous les jours")]
    [InlineData(ScheduleDays.Weekdays, "du lundi au vendredi")]
    [InlineData(ScheduleDays.Weekend, "le week-end")]
    [InlineData(ScheduleDays.Thursday, "le jeudi")]
    [InlineData(ScheduleDays.Monday | ScheduleDays.Thursday | ScheduleDays.Sunday, "lun., jeu., dim.")]
    public void DaysReadNaturally(ScheduleDays days, string expected) =>
        Assert.Equal(expected, ScheduleDayLabels.Describe(days));

    [Fact]
    public void TheRateComesFromTheHistoryOnceThereIsEnoughOfIt()
    {
        static CleanSummary Clean(int minutes, double area) =>
            new("c", "m", false, 0, 0, minutes, area, null, null, null, null, null, null, null);

        // Too little history, or only cleans too small to say anything: the default.
        Assert.Equal(CleanDurationEstimate.DefaultMinutesPerSquareMetre, CleanDurationEstimate.MinutesPerSquareMetre([Clean(128, 87.1), Clean(3, 0)]));
        // The median, not the mean: one clean interrupted after a long wait does not skew it.
        Assert.Equal(2.0, CleanDurationEstimate.MinutesPerSquareMetre([Clean(20, 10), Clean(30, 15), Clean(400, 10)]));
    }

    [Fact]
    public void VacuumThenMopAndASecondPassTakeLonger()
    {
        var minutes = CleanDurationEstimate.Minutes(
        [
            (10.0, new RoomSettings(CleanType.Vacuum)),
            (10.0, new RoomSettings(CleanType.VacuumThenMop)),
            (10.0, new RoomSettings(CleanType.Mop, MopPasses: 2)),
        ], 1.0);

        Assert.Equal(10 + 20 + 15, minutes);
    }

    [Fact]
    public void ASecondScheduleDueWhileTheFirstRunsOverlapsIt()
    {
        var first = Schedule(ScheduleDays.Monday, 10, 0);

        Assert.True(CleanDurationEstimate.Overlaps(first, 45, Schedule(ScheduleDays.Monday, 10, 29)));
        Assert.False(CleanDurationEstimate.Overlaps(first, 45, Schedule(ScheduleDays.Monday, 10, 45)));
        Assert.False(CleanDurationEstimate.Overlaps(first, 45, Schedule(ScheduleDays.Tuesday, 10, 29)));
        // Sunday late into Monday's early hours.
        Assert.True(CleanDurationEstimate.Overlaps(Schedule(ScheduleDays.Sunday, 23, 50), 30, Schedule(ScheduleDays.Monday, 0, 10)));
    }

    private static CleaningSchedule Schedule(ScheduleDays days, int hour, int minute) =>
        new(1, 1000000002, true, days, hour, minute, [new(10, new RoomSettings(CleanType.Vacuum))]);

    private static void AssertJson(string expected, JsonNode actual)
    {
        var expectedNode = JsonNode.Parse(expected);
        Assert.True(JsonNode.DeepEquals(expectedNode, actual), $"expected {expectedNode!.ToJsonString()}\n but got {actual.ToJsonString()}");
    }

    /// <summary>Answers every request with the same reply and keeps what was sent.</summary>
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
