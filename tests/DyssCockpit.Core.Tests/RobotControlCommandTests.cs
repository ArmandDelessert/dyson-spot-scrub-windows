using DyssCockpit.Core;

namespace DyssCockpit.Core.Tests;

/// <summary>
/// The commands besides the start sequence, each checked message by message against what the
/// official app sent in the captures of 2026-09-19 to 26 (the classic commands without the "h", "i"
/// and "time" the app adds, which carry no meaning for the robot). The few that were never captured
/// are pinned as they are, and say so.
/// </summary>
public class RobotControlCommandTests
{
    private static RecordingRobot Robot() => new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- Settings: a classic STATE-SET, then the jdm prop.set waited for --------------------

    [Fact]
    public async Task HotWaterForTheMopIsSetInBothDialects()
    {
        var robot = Robot();
        await robot.SetHotWaterMopAsync(true, ct: Ct);
        await robot.SetHotWaterMopAsync(false, ct: Ct);

        robot.AssertSent(
            ("STATE-SET", """{"msg":"STATE-SET","mode-reason":"RAPP","hotWaterMop":true}"""),
            ("prop.set", """{"hot_water_mop":1}"""),
            ("STATE-SET", """{"msg":"STATE-SET","mode-reason":"RAPP","hotWaterMop":false}"""),
            ("prop.set", """{"hot_water_mop":0}"""));
        Assert.Equal(RecordingRobot.Channel.Request, robot.Sent[1].Via);   // the setting waits for the robot's word
    }

    [Theory]
    [InlineData("hotWaterSwitch", "hot_water_switch")]
    [InlineData("detergent", "detergent")]
    [InlineData("alarm", "alarm")]
    public async Task EveryOnOffSettingIsTrueOrFalseClassicallyAndOneOrZeroInJdm(string classic, string jdm)
    {
        var robot = Robot();
        Func<bool, Task> set = classic switch
        {
            "hotWaterSwitch" => on => robot.SetHotWaterSwitchAsync(on, ct: Ct),
            "detergent" => on => robot.SetDetergentAsync(on, ct: Ct),
            _ => on => robot.SetAlarmAsync(on, ct: Ct),
        };

        await set(true);
        await set(false);

        robot.AssertSent(
            ("STATE-SET", $$"""{"msg":"STATE-SET","mode-reason":"RAPP","{{classic}}":true}"""),
            ("prop.set", $$"""{"{{jdm}}":1}"""),
            ("STATE-SET", $$"""{"msg":"STATE-SET","mode-reason":"RAPP","{{classic}}":false}"""),
            ("prop.set", $$"""{"{{jdm}}":0}"""));
    }

    [Fact]
    public async Task TheVolumeAndTheDryingAreNumbersOnBothSides()
    {
        var robot = Robot();
        await robot.SetVolumeAsync(40, ct: Ct);
        await robot.SetAirDryFrequencyAsync(4, ct: Ct);

        robot.AssertSent(
            ("STATE-SET", """{"msg":"STATE-SET","mode-reason":"RAPP","volume":40}"""),
            ("prop.set", """{"volume":40}"""),
            ("STATE-SET", """{"msg":"STATE-SET","mode-reason":"RAPP","airDryFrequency":4}"""),
            ("prop.set", """{"airdry_frequency":4}"""));
    }

    [Fact]
    public async Task TheMopIsWashedAfterEachRoomOrEveryFewMinutes()
    {
        var robot = Robot();
        await robot.SetBackWashPerRoomAsync(ct: Ct);
        await robot.SetBackWashByTimeAsync(15, ct: Ct);

        robot.AssertSent(
            ("STATE-SET", """{"msg":"STATE-SET","mode-reason":"RAPP","backWashType":"ROOM"}"""),
            ("prop.set", """{"back_wash_type":1}"""),
            ("STATE-SET", """{"msg":"STATE-SET","mode-reason":"RAPP","backWashType":"TIME","backWashTime":15}"""),
            ("prop.set", """{"back_wash_type":0,"back_wash_time":15}"""));
    }

    [Fact]
    public async Task OnlyWhenNeededIsAnIntervalOfAnHour()
    {
        // The app's ONLY_WHEN_NEEDED: the 60 minutes captured, not a value of its own.
        var robot = Robot();
        await robot.SetBackWashOnlyWhenNeededAsync(ct: Ct);

        robot.AssertSent(
            ("STATE-SET", """{"msg":"STATE-SET","mode-reason":"RAPP","backWashType":"TIME","backWashTime":60}"""),
            ("prop.set", """{"back_wash_type":0,"back_wash_time":60}"""));
    }

    [Fact]
    public async Task ExtendingTheWashBeforeACleanIsClassicOnly()
    {
        // From the app's settings model, never captured: no jdm counterpart is known.
        var robot = Robot();
        await robot.SetWashMopBeforeCleanAsync(true, ct: Ct);

        robot.AssertSent(("STATE-SET", """{"msg":"STATE-SET","mode-reason":"RAPP","washMopBeforeClean":true}"""));
    }

    // ---- Pause, abort, mapping ----------------------------------------------------------------

    [Fact]
    public async Task APauseIsAClassicPauseAndASetRoomCleanOfTwo()
    {
        var robot = Robot();
        await robot.PauseAsync(ct: Ct);

        robot.AssertSent(
            ("PAUSE", """{"msg":"PAUSE","mode-reason":"RAPP","cleaningMode":"zoneConfigured"}"""),
            ("service.set_room_clean", """{"ctrl_value":2,"clean_type":0,"room_ids":[]}"""));
    }

    [Theory]
    [InlineData("FULL_CLEAN_RUNNING")]
    [InlineData("FULL_CLEAN_PAUSED")]
    public async Task AnAbortSaysWhatTheRobotWasDoingAndSendsItToRecharge(string state)
    {
        var robot = Robot();
        await robot.AbortAsync(state, ct: Ct);

        robot.AssertSent(
            ("ABORT", $$"""{"msg":"ABORT","mode-reason":"RAPP","cleaningMode":"zoneConfigured","state":"{{state}}"}"""),
            ("service.start_recharge", "{}"));
    }

    [Fact]
    public async Task AMappingStartsWithTheLanguageOfTheNamesToCome()
    {
        var robot = Robot();
        await robot.StartMappingAsync("fr-CH", ct: Ct);

        robot.AssertSent(
            ("STATE-SET", """{"msg":"STATE-SET","mode-reason":"RAPP","mapLanguage":"fr-CH"}"""),
            ("START-MAPPING", """{"msg":"START-MAPPING","mode-reason":"RAPP"}"""),
            ("service.start_explore", """{"mode":0}"""));
    }

    // ---- The dock -------------------------------------------------------------------------------

    [Fact]
    public async Task EmptyingTheBinIsADockActionInBothDialects()
    {
        var robot = Robot();
        await robot.CollectDustAsync(ct: Ct);

        robot.AssertSent(
            ("START-DOCK-ACTION", """{"msg":"START-DOCK-ACTION","mode-reason":"RAPP","action":"COLLECT_DUST"}"""),
            ("service.start_station_act", """{"ctrl_value":1,"station_act":3}"""));
    }

    [Theory]
    [InlineData("DRYING_MOP", "DRY_MOP", 2)]        // captured
    [InlineData("WASHING_MOP", "WASH_MOP", 1)]      // inferred from the two captured pairs
    [InlineData("COLLECTING_DUST", "COLLECT_DUST", 3)]
    public async Task StoppingTheDockStopsWhatItIsDoing(string dockState, string action, int stationAct)
    {
        var robot = Robot();
        await robot.StopDockActionAsync(dockState, ct: Ct);

        robot.AssertSent(
            ("ABORT-DOCK-ACTION", $$"""{"msg":"ABORT-DOCK-ACTION","mode-reason":"RAPP","action":"{{action}}"}"""),
            ("service.start_station_act", $$"""{"ctrl_value":0,"station_act":{{stationAct}}}"""));
    }

    [Theory]
    [InlineData("IDLE")]
    [InlineData(null)]
    public async Task ADockDoingNothingIsSentNothing(string? dockState)
    {
        var robot = Robot();
        await robot.StopDockActionAsync(dockState, ct: Ct);

        Assert.Empty(robot.Sent);
    }

    [Fact]
    public async Task WashingAndDryingStartsTheWash()
    {
        // The action's name is the APK's; the pairing with station_act 1 is inferred.
        var robot = Robot();
        await robot.WashAndDryMopAsync(ct: Ct);

        robot.AssertSent(
            ("START-DOCK-ACTION", """{"msg":"START-DOCK-ACTION","mode-reason":"RAPP","action":"WASH_MOP"}"""),
            ("service.start_station_act", """{"ctrl_value":1,"station_act":1}"""));
    }

    // ---- Reading, and what was never captured --------------------------------------------------

    [Fact]
    public async Task TheStateAndThePropertiesAreAskedForAsTheAppDoes()
    {
        var robot = Robot();
        await robot.RequestCurrentStateAsync(ct: Ct);
        await robot.GetPropertiesAsync(["status", "fault"], ct: Ct);
        await robot.GetPropertiesAsync(ct: Ct);

        Assert.Equal(("REQUEST-CURRENT-STATE", RecordingRobot.Channel.Command), (robot.Sent[0].Method, robot.Sent[0].Via));
        Assert.Equal("""{"property":["status","fault"]}""", robot.Sent[1].Payload.ToJsonString());
        Assert.Equal(RobotCommands.AppPropertyNames.Length, robot.Sent[2].Payload["property"]!.AsArray().Count);
    }

    [Fact]
    public async Task TheCommandsNeverCapturedArePinnedAsTheyAre()
    {
        var robot = Robot();
        await robot.ResumeAsync("spotZoneConfigured", ct: Ct);
        await robot.StopAsync(ct: Ct);
        await robot.StartGlobalCleanAsync(ct: Ct);

        robot.AssertSent(
            ("RESUME", """{"msg":"RESUME","mode-reason":"RAPP","cleaningMode":"spotZoneConfigured"}"""),
            ("STOP", """{"msg":"STOP","mode-reason":"RAPP"}"""),
            ("START", """{"msg":"START","mode-reason":"RAPP","cleaningMode":"global","fullCleanType":"immediate"}"""));
    }
}
