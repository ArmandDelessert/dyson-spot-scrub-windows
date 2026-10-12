using System.Text.Json.Nodes;
using DyssCockpit.Core;
using DyssCockpit.Presentation.Services;
using DyssCockpit.Presentation.ViewModels;
using static DyssCockpit.TestSupport.RecordingRobot;

namespace DyssCockpit.Presentation.Tests;

/// <summary>
/// What each page sends the robot, with a robot that records it in place of the MQTT session. The
/// payloads themselves are pinned against the captures in the Core suite; what is checked here is
/// that each button sends the right one, with the robot's own state, the rooms picked and the
/// answers given — and that a refusal is said as such.
/// </summary>
public class SentToTheRobotTests
{
    private const string Refused = """{"code":0,"data":{"result":1}}""";

    private static (RobotHub Hub, RecordingRobot Robot) Hub(string? reply = null, FakeDialogs? dialogs = null, params (string UrlContains, string Json)[] routes)
    {
        var hub = TestHub.Create(dialogs ?? new FakeDialogs(), routes);
        var robot = reply is null ? new RecordingRobot() : new RecordingRobot(reply);
        hub.Robot = robot;
        return (hub, robot);
    }

    private static RobotState State(string json) => RobotState.Parse(json)!;

    private static string[] Methods(RecordingRobot robot) => [.. robot.Sent.Select(m => m.Method)];

    // ---- Réglages du robot -------------------------------------------------------------------

    [Fact]
    public void EachSettingTheUserChangesIsSentOnce()
    {
        var (hub, robot) = Hub();
        var vm = new RobotSettingsViewModel(hub);
        vm.Apply(State("""{"msg":"CURRENT-STATE","hotWaterMop":false,"detergent":true,"alarm":true,"airDryFrequency":3,"backWashType":"ROOM"}"""));
        Assert.Empty(robot.Sent);   // what the robot said is not said back to it

        vm.HotWaterMop = true;
        vm.Detergent = false;
        vm.HotWaterSwitch = true;
        vm.Alarm = false;
        vm.DryDuration = vm.DryOptions.Single(o => o.Hours == 5);
        vm.BackWash = vm.BackWashOptions.Single(o => o.Key == "TIME30");
        vm.WashMopBeforeClean = true;

        robot.AssertSent(
            ("STATE-SET", """{"msg":"STATE-SET","mode-reason":"RAPP","hotWaterMop":true}"""), ("prop.set", """{"hot_water_mop":1}"""),
            ("STATE-SET", """{"msg":"STATE-SET","mode-reason":"RAPP","detergent":false}"""), ("prop.set", """{"detergent":0}"""),
            ("STATE-SET", """{"msg":"STATE-SET","mode-reason":"RAPP","hotWaterSwitch":true}"""), ("prop.set", """{"hot_water_switch":1}"""),
            ("STATE-SET", """{"msg":"STATE-SET","mode-reason":"RAPP","alarm":false}"""), ("prop.set", """{"alarm":0}"""),
            ("STATE-SET", """{"msg":"STATE-SET","mode-reason":"RAPP","airDryFrequency":5}"""), ("prop.set", """{"airdry_frequency":5}"""),
            ("STATE-SET", """{"msg":"STATE-SET","mode-reason":"RAPP","backWashType":"TIME","backWashTime":30}"""), ("prop.set", """{"back_wash_type":0,"back_wash_time":30}"""),
            ("STATE-SET", """{"msg":"STATE-SET","mode-reason":"RAPP","washMopBeforeClean":true}"""));
        Assert.Equal("", hub.Message);
    }

    [Theory]
    [InlineData("ROOM", """{"back_wash_type":1}""")]
    [InlineData("TIME15", """{"back_wash_type":0,"back_wash_time":15}""")]
    [InlineData("ONLY_WHEN_NEEDED", """{"back_wash_type":0,"back_wash_time":60}""")]
    public void EachIntervalIsSentAsTheRobotKnowsIt(string key, string jdm)
    {
        var (hub, robot) = Hub();
        var vm = new RobotSettingsViewModel(hub);
        vm.Apply(State("""{"msg":"CURRENT-STATE","backWashType":"TIME","backWashTime":30}"""));

        vm.BackWash = vm.BackWashOptions.Single(o => o.Key == key);

        Assert.Equal(["STATE-SET", "prop.set"], Methods(robot));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(jdm), robot.Sent[1].Payload), robot.Sent[1].Payload.ToJsonString());
    }

    [Fact]
    public async Task TheVolumeIsSentWhenTheSliderIsLetGo()
    {
        var (hub, robot) = Hub();
        var vm = new RobotSettingsViewModel(hub);

        vm.Volume = 40;
        Assert.Empty(robot.Sent);
        await vm.ApplyVolumeCommand.ExecuteAsync(null);

        robot.AssertSent(
            ("STATE-SET", """{"msg":"STATE-SET","mode-reason":"RAPP","volume":40}"""),
            ("prop.set", """{"volume":40}"""));
    }

    // ---- État --------------------------------------------------------------------------------

    [Fact]
    public async Task PauseThenResumeInTheModeTheRobotIsCleaningIn()
    {
        var (hub, robot) = Hub();
        var vm = new StatusViewModel(hub);

        vm.Apply(State("""{"msg":"CURRENT-STATE","state":"FULL_CLEAN_RUNNING","currentCleaningMode":"spotZoneConfigured"}"""));
        await vm.PauseResumeCommand.ExecuteAsync(null);
        vm.Apply(State("""{"msg":"CURRENT-STATE","state":"FULL_CLEAN_PAUSED","currentCleaningMode":"spotZoneConfigured"}"""));
        await vm.PauseResumeCommand.ExecuteAsync(null);

        Assert.Equal(["PAUSE", "service.set_room_clean", "RESUME"], Methods(robot));
        Assert.Equal("spotZoneConfigured", robot.Sent[2].Payload["cleaningMode"]!.GetValue<string>());
        // The journal, newest first, after the time.
        Assert.Equal(["reprise", "pause"], hub.Log.Select(l => l[(l.IndexOf(' ', StringComparison.Ordinal) + 1)..]));
    }

    [Fact]
    public async Task TheReturnToTheDockSaysWhatTheRobotWasDoing()
    {
        var (hub, robot) = Hub();
        var vm = new StatusViewModel(hub);
        vm.Apply(State("""{"msg":"CURRENT-STATE","state":"FULL_CLEAN_PAUSED","currentCleaningMode":"zoneConfigured"}"""));

        await vm.AbortCommand.ExecuteAsync(null);

        robot.AssertSent(
            ("ABORT", """{"msg":"ABORT","mode-reason":"RAPP","cleaningMode":"zoneConfigured","state":"FULL_CLEAN_PAUSED"}"""),
            ("service.start_recharge", "{}"));
    }

    [Fact]
    public async Task TheDockButtonStartsAWashOrStopsWhatTheDockIsDoing()
    {
        var (hub, robot) = Hub();
        var vm = new StatusViewModel(hub);

        vm.Apply(State("""{"msg":"CURRENT-STATE","state":"INACTIVE_CHARGING","dockState":"IDLE"}"""));
        await vm.WashDryCommand.ExecuteAsync(null);
        vm.Apply(State("""{"msg":"CURRENT-STATE","state":"INACTIVE_CHARGING","dockState":"DRYING_MOP"}"""));
        await vm.WashDryCommand.ExecuteAsync(null);
        await vm.CollectDustCommand.ExecuteAsync(null);

        Assert.Equal(["START-DOCK-ACTION", "service.start_station_act", "ABORT-DOCK-ACTION", "service.start_station_act", "START-DOCK-ACTION", "service.start_station_act"], Methods(robot));
        Assert.Equal(["WASH_MOP", "DRY_MOP", "COLLECT_DUST"], robot.Sent.Where(m => m.Via == Channel.Command).Select(m => m.Payload["action"]!.GetValue<string>()));
    }

    // ---- Nettoyage ---------------------------------------------------------------------------

    private static readonly (string Id, string Name, string? Type)[] Rooms =
        [("10", "Cuisine", "kitchen"), ("12", "Chambre", "bedroom"), ("13", "Couloir", "hallway")];

    /// <summary>The robot's own room_preference, which the start rewrites: one twelve-element entry per room.</summary>
    private const string Preferences = """
        {"code":0,"data":{"room":[[10,"Cuisine",0,0,0,0,0,0,0,0,0,0],[12,"Chambre",0,0,0,0,0,0,0,0,0,0],[13,"Couloir",0,0,0,0,0,0,0,0,0,0]],"uv_switch":[]}}
        """;

    private static async Task<(CleaningViewModel Vm, RobotHub Hub, RecordingRobot Robot)> CleaningAsync(string? reply = null)
    {
        var (hub, robot) = Hub(reply, null,
            ("persistent-map-metadata", "[" + TestHub.Map("1000000002", "Étage", isCurrent: true, Rooms) + "]"),
            ("persistent-maps", TestHub.EmptyPersistentMap),
            ("live-maps/mapping", """{"dimensions":{"width":2,"height":2,"resolution":0.5,"offsetX":0,"offsetY":0},"mapData":[10,10,12,12]}"""),
            ("live-maps/cleaning", """{"cleanPath":[],"obstacles":[],"dirt":[]}"""));
        var vm = new CleaningViewModel(hub, new MapCatalog(hub), new AppSettings());
        await vm.LoadMapsAsync();
        vm.Apply(State("""{"msg":"CURRENT-STATE","state":"INACTIVE_CHARGING","dockState":"IDLE"}"""));
        return (vm, hub, robot);
    }

    [Fact]
    public async Task TheRoomsPickedAreCleanedInTheOrderTheyWereTicked()
    {
        var (vm, _, robot) = await CleaningAsync(Preferences);
        vm.ToggleZone("13");
        vm.ToggleZone("10");

        await vm.StartCleanCommand.ExecuteAsync(null);

        Assert.Equal(["service.get_preference", "service.set_preference", "START", "service.set_cur_map", "service.set_room_clean"], Methods(robot));
        Assert.Equal("""{"map_id":1000000002}""", robot.Sent[0].Payload.ToJsonString());
        // Selected (index 8) and their order (index 10); the room left out is unticked.
        var prefs = robot.Sent[1].Payload["room_preference"]!.AsArray().Select(r => (Id: r![0]!.GetValue<int>(), Selected: r[8]!.GetValue<int>(), Order: r[10]!.GetValue<int>()));
        Assert.Equal([(10, 1, 2), (12, 0, 0), (13, 1, 1)], prefs);
        Assert.Equal(["13", "10"], robot.Sent[2].Payload["cleaningProgramme"]!["unorderedZones"]!.AsArray().Select(z => z!.GetValue<string>()));
        Assert.Equal("""{"ctrl_value":1,"clean_type":0,"room_ids":[13,10]}""", robot.Sent[4].Payload.ToJsonString());
        Assert.Equal(["Couloir", "Cuisine"], vm.LastLaunch!.Rooms);
    }

    [Fact]
    public async Task NothingIsSentWithoutARoom()
    {
        var (vm, hub, robot) = await CleaningAsync();

        await vm.StartCleanCommand.ExecuteAsync(null);

        Assert.Empty(robot.Sent);
        Assert.Equal("Sélectionnez au moins une pièce.", hub.Message);
    }

    [Fact]
    public async Task ADrawnZoneIsSentWithItsCornersAndItsSettings()
    {
        var (vm, _, robot) = await CleaningAsync();
        vm.SpotDrawn(new(0, 0), new(1.5, 1));
        vm.SpotCleanType = vm.SpotCleanTypes.Single(o => o.Value == CleanType.Vacuum);

        await vm.StartSpotCleanCommand.ExecuteAsync(null);

        Assert.Equal(["START", "service.set_cur_map", "service.set_areas_start"], Methods(robot));
        var programme = robot.Sent[0].Payload["cleaningProgramme"]!;
        Assert.Equal("1000000002", programme["persistentMapId"]!.GetValue<string>());
        Assert.Equal("vacuum", programme["defaultSpotZoneSettings"]!["cleanType"]!.GetValue<string>());
        Assert.Equal("[[1.5,1,0,1,0,0,1.5,0]]", robot.Sent[2].Payload["zone_points"]!.ToJsonString());
        Assert.Empty(vm.LastLaunch!.Rooms);
    }

    // ---- Horaires ----------------------------------------------------------------------------

    private const string ActiveId = "1000000002";

    private static async Task<(SchedulesViewModel Vm, RobotHub Hub, RecordingRobot Robot)> SchedulesAsync(string? reply = null, FakeDialogs? dialogs = null)
    {
        var monday = new JsonObject
        {
            ["groupId"] = 7, ["days"] = new JsonArray(1), ["startTime"] = "09:00", ["weeklyRepeat"] = true, ["enabled"] = true,
            ["settings"] = new JsonObject
            {
                ["persistentMapId"] = ActiveId,
                ["zones"] = new JsonArray(new JsonObject
                {
                    ["id"] = "10", ["name"] = "Cuisine", ["type"] = "", ["isSelected"] = true, ["order"] = 1,
                    ["settings"] = new JsonObject { ["cleanType"] = "vacuum", ["cleaningStrategy"] = "auto", ["waterLevel"] = "low", ["mopPasses"] = 1 },
                }),
            },
        };
        var (hub, robot) = Hub(reply, dialogs,
            ("persistent-map-metadata", "[" + TestHub.Map(ActiveId, "Étage", isCurrent: true, ("10", "Cuisine", "kitchen"), ("11", "Bureau", "office")) + "]"),
            ("unifiedscheduler", new JsonObject { ["enabled"] = true, ["serial"] = "SERIAL", ["events"] = new JsonArray(monday) }.ToJsonString()),
            // Not this machine's zone, and without summer time: the offset sent must be the robot's.
            ("timezone", """{"timezone":"Asia/Tokyo"}"""));
        hub.Connected = true;
        var maps = new MapCatalog(hub);
        var vm = new SchedulesViewModel(hub, maps, () => []) { ReloadDelay = TimeSpan.Zero };
        await maps.LoadAsync();
        await vm.LoadAsync();
        return (vm, hub, robot);
    }

    [Fact]
    public async Task ANewScheduleIsAddedInTheRobotsTimeZone()
    {
        var dialogs = new FakeDialogs
        {
            EditSchedule = editor =>
            {
                editor.Days[2].IsChecked = true;
                editor.Rooms.Single(r => r.Id == "11").Selected = true;
                editor.Hour = 14;
                editor.Minute = 5;
                editor.Repeats = false;
                return true;
            },
        };
        var (vm, _, robot) = await SchedulesAsync(dialogs: dialogs);

        await vm.NewCommand.ExecuteAsync(null);

        var sent = Assert.Single(robot.Sent);
        Assert.Equal(("service.add_order", Channel.Request), (sent.Method, sent.Via));
        Assert.Equal((1, 14, 5, 0, 1000000002L, 9 * 3600),
            (sent.Payload["enable"]!.GetValue<int>(), sent.Payload["hour"]!.GetValue<int>(), sent.Payload["minute"]!.GetValue<int>(),
             sent.Payload["repeat"]!.GetValue<int>(), sent.Payload["map_id"]!.GetValue<long>(), sent.Payload["time_zone"]!.GetValue<int>()));
        Assert.Equal("Horaire de 14:05 créé.", vm.Status);
    }

    [Fact]
    public async Task TheCheckBoxSendsTheSameScheduleTurnedOff()
    {
        var (vm, _, robot) = await SchedulesAsync();

        await vm.ToggleCommand.ExecuteAsync(vm.Items.Single());

        var sent = Assert.Single(robot.Sent);
        Assert.Equal("service.add_order", sent.Method);
        Assert.Equal((7L, 0, 9, 0, 1), (sent.Payload["id"]!.GetValue<long>(), sent.Payload["enable"]!.GetValue<int>(),
            sent.Payload["hour"]!.GetValue<int>(), sent.Payload["minute"]!.GetValue<int>(), sent.Payload["repeat"]!.GetValue<int>()));
        Assert.Equal("Horaire désactivé.", vm.Status);
    }

    [Fact]
    public async Task ADeletionIsSentOnlyOnceConfirmed()
    {
        var dialogs = new FakeDialogs();
        var (vm, _, robot) = await SchedulesAsync(dialogs: dialogs);

        await vm.DeleteCommand.ExecuteAsync(vm.Items.Single());
        Assert.Empty(robot.Sent);

        dialogs.Confirm = (_, _) => true;
        await vm.DeleteCommand.ExecuteAsync(vm.Items.Single());

        robot.AssertSent(("service.del_order", """{"id":7}"""));
        Assert.Equal("Horaire supprimé.", vm.Status);
    }

    [Fact]
    public async Task ARefusalIsSaidAndTheListKeepsTheScheduleAsItWas()
    {
        var (vm, _, robot) = await SchedulesAsync(Refused);

        await vm.ToggleCommand.ExecuteAsync(vm.Items.Single());

        Assert.Single(robot.Sent);
        Assert.Equal("Le robot a refusé.", vm.Status);
        Assert.True(vm.Items.Single().Enabled);
    }

    // ---- Cartes ------------------------------------------------------------------------------

    private static async Task<(MapManagerViewModel Vm, RecordingRobot Robot)> MapsAsync(FakeDialogs dialogs, string? reply = null)
    {
        var (hub, robot) = Hub(reply, dialogs,
            ("persistent-map-metadata", "[" + TestHub.Map(ActiveId, "Étage", isCurrent: true, ("10", "Cuisine", "kitchen")) + "]"),
            ("persistent-maps", TestHub.EmptyPersistentMap),
            ("live-maps/mapping", """{"dimensions":{"width":2,"height":2,"resolution":0.5,"offsetX":0,"offsetY":0},"mapData":[10,10,11,11]}"""));
        var vm = new MapManagerViewModel(hub, new AppSettings());
        await vm.LoadAsync(ActiveId);
        return (vm, robot);
    }

    [Fact]
    public async Task ARenamedMapIsSentWithItsNewNameTrimmed()
    {
        var (vm, robot) = await MapsAsync(new FakeDialogs { AskText = (_, _, _) => "  Rez-de-chaussée " });

        await vm.RenameMapCommand.ExecuteAsync(null);

        robot.AssertSent(("service.rename_map", """{"map_id":1000000002,"map_name":"Rez-de-chaussée"}"""));
        Assert.Equal("Carte renommée en « Rez-de-chaussée ».", vm.Status);
    }

    [Fact]
    public async Task AMapEditTheRobotRefusesIsSaidSo()
    {
        var (vm, robot) = await MapsAsync(new FakeDialogs { AskText = (_, _, _) => "Rez" }, Refused);

        await vm.RenameMapCommand.ExecuteAsync(null);

        Assert.Single(robot.Sent);
        Assert.Equal("Le robot a refusé la modification.", vm.Status);
    }

    [Fact]
    public async Task AMappingNamesItsRoomsInTheAccountsLanguage()
    {
        var (vm, robot) = await MapsAsync(new FakeDialogs { Confirm = (_, _) => true });

        await vm.StartMappingCommand.ExecuteAsync(null);

        Assert.Equal(["STATE-SET", "START-MAPPING", "service.start_explore"], Methods(robot));
        Assert.Equal("fr-CH", robot.Sent[0].Payload["mapLanguage"]!.GetValue<string>());
    }
}
