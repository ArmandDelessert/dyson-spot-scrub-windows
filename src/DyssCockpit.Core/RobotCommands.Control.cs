using System.Text.Json.Nodes;

namespace DyssCockpit.Core;

/// <summary>
/// The other commands of the official app, beside the start sequence: written against
/// <see cref="IRobotCommands"/> like it, so each payload can be checked against the captures without a
/// broker, and so the view models can be tested with a robot that only records what it is sent.
/// <see cref="RobotMqttClient"/> carries them to the robot.
/// </summary>
public static partial class RobotCommands
{    // ---- Commands taken from captures of the official app --------------------
    //
    // Payloads below were observed on 2026-09-19 while the official app drove this robot.
    // The section further down holds commands that only appear as APK strings and are unverified.
    // The start sequence (set_preference, START, set_cur_map, set_room_clean) is in RobotCommands.

    public static Task RequestCurrentStateAsync(this IRobotCommands robot, CancellationToken ct = default) =>
        robot.PublishCommandAsync(new JsonObject { ["msg"] = "REQUEST-CURRENT-STATE" }, ct);

    /// <summary>
    /// Stops a dock action such as mop drying, which runs for hours after a clean. Observed with
    /// action DRY_MOP, sent together with <see cref="StartStationActionAsync"/>(0, 2).
    /// </summary>
    public static Task AbortDockActionAsync(this IRobotCommands robot, string action = "DRY_MOP", CancellationToken ct = default) =>
        robot.PublishCommandAsync(new JsonObject
        {
            ["msg"] = "ABORT-DOCK-ACTION",
            ["mode-reason"] = "RAPP",
            ["action"] = action,
        }, ct);

    /// <summary>jdm counterpart of a dock action. Observed as ctrlValue 0, stationAct 2 to stop drying.</summary>
    public static Task StartStationActionAsync(this IRobotCommands robot, int ctrlValue, int stationAct, CancellationToken ct = default) =>
        robot.PublishJdmAsync("service.start_station_act", new JsonObject
        {
            ["ctrl_value"] = ctrlValue,
            ["station_act"] = stationAct,
        }, ct);

    /// <summary>
    /// Sets the robot's own time zone, as an IANA name such as "Europe/Amsterdam". The robot answers
    /// data.result 0 on success and 1 on refusal; it refuses while a dock action is running.
    /// </summary>
    public static Task SetRobotTimeZoneAsync(this IRobotCommands robot, string ianaTimeZone, CancellationToken ct = default) =>
        robot.PublishJdmAsync("service.set_robot_time_zone", new JsonObject { ["time_zone"] = ianaTimeZone }, ct);

    public static Task GetMapListAsync(this IRobotCommands robot, CancellationToken ct = default) =>
        robot.PublishJdmAsync("service.get_map_list", ct: ct);

    public static Task GetRoomPreferenceAsync(this IRobotCommands robot, long mapId, CancellationToken ct = default) =>
        robot.PublishJdmAsync("service.get_preference", new JsonObject { ["map_id"] = mapId }, ct);

    /// <summary>Reads robot properties. The app always passes an explicit list of names.</summary>
    public static Task GetPropertiesAsync(this IRobotCommands robot, IEnumerable<string>? propertyNames = null, CancellationToken ct = default)
    {
        var names = new JsonArray();
        foreach (var n in propertyNames ?? AppPropertyNames) names.Add(n);
        return robot.PublishJdmAsync("prop.get", new JsonObject { ["property"] = names }, ct);
    }

    /// <summary>The property list the official app requests.</summary>
    public static readonly string[] AppPropertyNames =
    [
        "airdry_frequency", "alarm", "auto_water_complete_flag", "auto_water_self_check",
        "back_wash_time", "back_wash_type", "charge_state", "child_lock", "clean_wash_attachment",
        "cleaning_time", "current_map_id", "detergent", "detergent_consumed", "dock_hypa",
        "dust_auto_state", "empty_bin_time", "empty_bin_type", "fault", "global_clean_status",
        "hot_water_mop", "hot_water_switch", "hypa", "ioniser", "main_brush", "mop_life",
        "mop_pad_life", "oob_state", "quantity", "quiet_begin_time", "quiet_end_time",
        "quiet_is_open", "robot_auto_updown_type", "side_brush", "station_act", "status",
        "store_demo_mode", "sweep_type", "taskBeginTs", "voice_type", "volume",
        "wash_back_frequency", "work_mode",
    ];

    // ---- Pause, abort, mapping (captured 2026-09-19 evening) --------------------

    /// <summary>
    /// Pauses the running clean. The app pairs the classic PAUSE with a jdm set_room_clean whose
    /// ctrl_value is 2 (1 starts, 2 pauses). The robot answers with state FULL_CLEAN_PAUSED.
    /// </summary>
    public static async Task PauseAsync(this IRobotCommands robot, string cleaningMode = "zoneConfigured", CancellationToken ct = default)
    {
        await robot.PublishCommandAsync(new JsonObject
        {
            ["msg"] = "PAUSE",
            ["mode-reason"] = "RAPP",
            ["cleaningMode"] = cleaningMode,
        }, ct).ConfigureAwait(false);
        await robot.PublishJdmAsync("service.set_room_clean", new JsonObject
        {
            ["ctrl_value"] = 2,
            ["clean_type"] = 0,
            ["room_ids"] = new JsonArray(),
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Aborts the current clean and sends the robot home. The classic ABORT carries the robot's
    /// current state (FULL_CLEAN_RUNNING or FULL_CLEAN_PAUSED) and is paired with jdm
    /// service.start_recharge. The robot goes to state ABORTED, then INACTIVE_DISCHARGING or
    /// INACTIVE_CHARGING once docked.
    /// </summary>
    public static async Task AbortAsync(this IRobotCommands robot, string currentState, string cleaningMode = "zoneConfigured", CancellationToken ct = default)
    {
        await robot.PublishCommandAsync(new JsonObject
        {
            ["msg"] = "ABORT",
            ["mode-reason"] = "RAPP",
            ["cleaningMode"] = cleaningMode,
            ["state"] = currentState,
        }, ct).ConfigureAwait(false);
        await robot.PublishJdmAsync("service.start_recharge", ct: ct).ConfigureAwait(false);
    }

    /// <summary>Alias kept for callers that only want the robot home.</summary>
    public static Task ReturnToDockAsync(this IRobotCommands robot, string currentState = "FULL_CLEAN_RUNNING", CancellationToken ct = default) =>
        robot.AbortAsync(currentState, ct: ct);

    /// <summary>
    /// Starts building a new map. The app first sets the map language, then sends START-MAPPING
    /// paired with jdm service.start_explore. States: MAPPING_RUNNING, MAPPING_FINISHED, and the
    /// new persistentMapId appears in CURRENT-STATE once event.BuildMapFinish.post has fired.
    /// </summary>
    public static async Task StartMappingAsync(this IRobotCommands robot, string mapLanguage, CancellationToken ct = default)
    {
        await robot.PublishCommandAsync(new JsonObject
        {
            ["msg"] = "STATE-SET",
            ["mode-reason"] = "RAPP",
            ["mapLanguage"] = mapLanguage,
        }, ct).ConfigureAwait(false);
        await robot.PublishCommandAsync(new JsonObject { ["msg"] = "START-MAPPING", ["mode-reason"] = "RAPP" }, ct).ConfigureAwait(false);
        await robot.PublishJdmAsync("service.start_explore", new JsonObject { ["mode"] = 0 }, ct).ConfigureAwait(false);
    }

    // ---- Dock actions --------------------------------------------------------------
    //
    // Classic START-DOCK-ACTION / ABORT-DOCK-ACTION are paired with jdm service.start_station_act
    // {ctrl_value: 1 start / 0 stop, station_act: 1 wash, 2 dry, 3 collect dust}. Captured: collect
    // dust (1, 3) and stopping the drying (0, 2). Washing as 1 is inferred from the APK's enum order
    // WASH_MOP, DRY_MOP, COLLECT_DUST.

    public const int StationActWash = 1;
    public const int StationActDry = 2;
    public const int StationActCollectDust = 3;

    private static async Task DockActionAsync(this IRobotCommands robot, bool start, string action, int stationAct, CancellationToken ct)
    {
        await robot.PublishCommandAsync(new JsonObject
        {
            ["msg"] = start ? "START-DOCK-ACTION" : "ABORT-DOCK-ACTION",
            ["mode-reason"] = "RAPP",
            ["action"] = action,
        }, ct).ConfigureAwait(false);
        await robot.StartStationActionAsync(start ? 1 : 0, stationAct, ct).ConfigureAwait(false);
    }

    /// <summary>Empties the robot's bin into the dock now. Captured from the app.</summary>
    public static Task CollectDustAsync(this IRobotCommands robot, CancellationToken ct = default) => robot.DockActionAsync(true, "COLLECT_DUST", StationActCollectDust, ct);

    /// <summary>"Laver et sécher" in the app: washes the mop roller, drying follows. Action name from the APK, pairing inferred.</summary>
    public static Task WashAndDryMopAsync(this IRobotCommands robot, CancellationToken ct = default) => robot.DockActionAsync(true, "WASH_MOP", StationActWash, ct);

    /// <summary>Stops mop drying. Captured from the app.</summary>
    public static Task StopDryingAsync(this IRobotCommands robot, CancellationToken ct = default) => robot.DockActionAsync(false, "DRY_MOP", StationActDry, ct);

    /// <summary>Stops mop washing. Inferred from the two captured pairs.</summary>
    public static Task StopWashingAsync(this IRobotCommands robot, CancellationToken ct = default) => robot.DockActionAsync(false, "WASH_MOP", StationActWash, ct);

    /// <summary>Stops whatever the dock is doing, according to the dockState of the last CURRENT-STATE.</summary>
    public static Task StopDockActionAsync(this IRobotCommands robot, string? dockState, CancellationToken ct = default) => dockState switch
    {
        "DRYING_MOP" => robot.StopDryingAsync(ct),
        "WASHING_MOP" => robot.StopWashingAsync(ct),
        "COLLECTING_DUST" => robot.DockActionAsync(false, "COLLECT_DUST", StationActCollectDust, ct),
        _ => Task.CompletedTask,
    };

    // ---- Settings: the app writes every setting in both dialects at once ---------

    /// <summary>
    /// Writes one setting the way the app does: a classic STATE-SET and a jdm prop.set carrying
    /// the same value, and waits for the jdm acknowledgement. Booleans are 0/1 on the jdm side.
    /// </summary>
    public static async Task<JsonObject> SetSettingAsync(this IRobotCommands robot, string classicName, JsonNode classicValue, string jdmName, JsonNode jdmValue, CancellationToken ct = default)
    {
        await robot.PublishCommandAsync(new JsonObject
        {
            ["msg"] = "STATE-SET",
            ["mode-reason"] = "RAPP",
            [classicName] = classicValue,
        }, ct).ConfigureAwait(false);
        return await robot.RequestJdmAsync("prop.set", new JsonObject { [jdmName] = jdmValue }, ct: ct).ConfigureAwait(false);
    }

    public static Task<JsonObject> SetHotWaterMopAsync(this IRobotCommands robot, bool on, CancellationToken ct = default) =>
        robot.SetSettingAsync("hotWaterMop", on, "hot_water_mop", on ? 1 : 0, ct);

    public static Task<JsonObject> SetHotWaterSwitchAsync(this IRobotCommands robot, bool on, CancellationToken ct = default) =>
        robot.SetSettingAsync("hotWaterSwitch", on, "hot_water_switch", on ? 1 : 0, ct);

    public static Task<JsonObject> SetDetergentAsync(this IRobotCommands robot, bool on, CancellationToken ct = default) =>
        robot.SetSettingAsync("detergent", on, "detergent", on ? 1 : 0, ct);

    public static Task<JsonObject> SetAlarmAsync(this IRobotCommands robot, bool on, CancellationToken ct = default) =>
        robot.SetSettingAsync("alarm", on, "alarm", on ? 1 : 0, ct);

    /// <summary>0 to 100.</summary>
    public static Task<JsonObject> SetVolumeAsync(this IRobotCommands robot, int volume, CancellationToken ct = default) =>
        robot.SetSettingAsync("volume", volume, "volume", volume, ct);

    /// <summary>3 to 5 as offered by the app.</summary>
    public static Task<JsonObject> SetAirDryFrequencyAsync(this IRobotCommands robot, int level, CancellationToken ct = default) =>
        robot.SetSettingAsync("airDryFrequency", level, "airdry_frequency", level, ct);

    /// <summary>Wash the mop after every room. Captured.</summary>
    public static Task<JsonObject> SetBackWashPerRoomAsync(this IRobotCommands robot, CancellationToken ct = default) =>
        robot.SetSettingAsync("backWashType", "ROOM", "back_wash_type", 1, ct);

    /// <summary>
    /// "Uniquement si nécessaire": the robot only returns to refill or empty. In the decompiled app
    /// this option is the interval enum's ONLY_WHEN_NEEDED entry with 60 minutes, and it is sent as
    /// backWashType TIME with backWashTime 60, which matches the 15, 30, 60 values captured.
    /// </summary>
    public static Task<JsonObject> SetBackWashOnlyWhenNeededAsync(this IRobotCommands robot, CancellationToken ct = default) =>
        robot.SetBackWashByTimeAsync(60, ct);

    /// <summary>
    /// "Prolonger les préparatifs de lavage". A classic STATE-SET field (washMopBeforeClean) found in
    /// the app's settings model; no jdm counterpart is known, so only the classic message is sent.
    /// </summary>
    public static Task SetWashMopBeforeCleanAsync(this IRobotCommands robot, bool on, CancellationToken ct = default) =>
        robot.PublishCommandAsync(new JsonObject
        {
            ["msg"] = "STATE-SET",
            ["mode-reason"] = "RAPP",
            ["washMopBeforeClean"] = on,
        }, ct);

    /// <summary>Wash the mop every N minutes; the app offers 15, 30 and 60.</summary>
    public static async Task<JsonObject> SetBackWashByTimeAsync(this IRobotCommands robot, int minutes, CancellationToken ct = default)
    {
        await robot.PublishCommandAsync(new JsonObject
        {
            ["msg"] = "STATE-SET",
            ["mode-reason"] = "RAPP",
            ["backWashType"] = "TIME",
            ["backWashTime"] = minutes,
        }, ct).ConfigureAwait(false);
        return await robot.RequestJdmAsync("prop.set", new JsonObject { ["back_wash_time"] = minutes, ["back_wash_type"] = 0 }, ct: ct).ConfigureAwait(false);
    }

    // ---- Commands not observed, taken from APK strings -----------------------

    /// <summary>Starts a clean of the whole map. Not observed: the app uses zones even for a full clean.</summary>
    public static Task StartGlobalCleanAsync(this IRobotCommands robot, CancellationToken ct = default) =>
        robot.PublishCommandAsync(new JsonObject
        {
            ["msg"] = "START",
            ["mode-reason"] = "RAPP",
            ["cleaningMode"] = "global",
            ["fullCleanType"] = "immediate",
        }, ct);

    public static Task RequestCurrentFaultsAsync(this IRobotCommands robot, CancellationToken ct = default) =>
        robot.PublishCommandAsync(new JsonObject { ["msg"] = "REQUEST-CURRENT-FAULTS" }, ct);

    /// <summary>Not observed. The jdm counterpart is probably set_room_clean with another ctrl_value.</summary>
    public static Task ResumeAsync(this IRobotCommands robot, string cleaningMode = "zoneConfigured", CancellationToken ct = default) =>
        robot.PublishCommandAsync(new JsonObject { ["msg"] = "RESUME", ["mode-reason"] = "RAPP", ["cleaningMode"] = cleaningMode }, ct);

    /// <summary>Not observed. The app stops a clean with PAUSE followed by ABORT, never STOP.</summary>
    public static Task StopAsync(this IRobotCommands robot, CancellationToken ct = default) =>
        robot.PublishCommandAsync(new JsonObject { ["msg"] = "STOP", ["mode-reason"] = "RAPP" }, ct);
}
