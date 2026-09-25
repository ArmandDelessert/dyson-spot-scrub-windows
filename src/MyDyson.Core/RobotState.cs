using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace MyDyson.Core;

public sealed record ActiveFault(
    [property: JsonPropertyName("faultCode")] string FaultCode,
    [property: JsonPropertyName("nextActionRequired")] string? NextActionRequired)
{
    /// <summary>The 21xx family with LOG_ONLY are status indicators, not problems.</summary>
    public bool IsStatusIndicator => NextActionRequired == "LOG_ONLY";

    /// <summary>A fault the user must clear before the robot continues, such as 589 (locate failure).</summary>
    public bool NeedsUser => NextActionRequired == "WAIT_TO_CLEAR";
}

public sealed record Consumable(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("usage")] int? Usage,
    [property: JsonPropertyName("needsRefill")] bool? NeedsRefill);

public sealed record DoNotDisturbMode(
    [property: JsonPropertyName("isOn")] bool IsOn,
    [property: JsonPropertyName("startTime")] string? StartTime,
    [property: JsonPropertyName("endTime")] string? EndTime);

/// <summary>Robot position in metres relative to the dock. Ids increase monotonically.</summary>
public sealed record RobotPosition(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("x")] double X,
    [property: JsonPropertyName("y")] double Y,
    [property: JsonPropertyName("angle")] double Angle,
    [property: JsonPropertyName("update")] int? Update);

/// <summary>
/// The CURRENT-STATE message of the classic dialect, 29 fields observed on the RB05.
/// Every field is optional because the robot also sends position-only messages of the same type.
/// </summary>
public sealed record RobotState
{
    [JsonPropertyName("time")] public DateTimeOffset? Time { get; init; }
    [JsonPropertyName("state")] public string? State { get; init; }
    [JsonPropertyName("batteryChargeLevel")] public int? BatteryChargeLevel { get; init; }
    [JsonPropertyName("dockState")] public string? DockState { get; init; }
    [JsonPropertyName("fullCleanAction")] public string? FullCleanAction { get; init; }
    [JsonPropertyName("currentCleaningMode")] public string? CurrentCleaningMode { get; init; }
    [JsonPropertyName("defaultCleaningMode")] public string? DefaultCleaningMode { get; init; }
    [JsonPropertyName("currentCleaningStrategy")] public string? CurrentCleaningStrategy { get; init; }
    [JsonPropertyName("defaultCleaningStrategy")] public string? DefaultCleaningStrategy { get; init; }
    [JsonPropertyName("cleanDuration")] public int? CleanDurationSeconds { get; init; }
    [JsonPropertyName("persistentMapId")] public string? PersistentMapId { get; init; }
    [JsonPropertyName("activeFaults")] public List<ActiveFault>? ActiveFaults { get; init; }
    [JsonPropertyName("outOfBoxState")] public string? OutOfBoxState { get; init; }
    [JsonPropertyName("consumables")] public List<Consumable>? Consumables { get; init; }
    [JsonPropertyName("globalPosition")] public List<RobotPosition>? GlobalPosition { get; init; }
    [JsonPropertyName("doNotDisturbMode")] public DoNotDisturbMode? DoNotDisturbMode { get; init; }

    // Dock and mop settings
    [JsonPropertyName("hotWaterMop")] public bool? HotWaterMop { get; init; }
    [JsonPropertyName("hotWaterSwitch")] public bool? HotWaterSwitch { get; init; }
    [JsonPropertyName("detergent")] public bool? Detergent { get; init; }
    [JsonPropertyName("backWashType")] public string? BackWashType { get; init; }
    [JsonPropertyName("backWashTime")] public int? BackWashTime { get; init; }
    [JsonPropertyName("backWashFrequency")] public int? BackWashFrequency { get; init; }
    [JsonPropertyName("airDryFrequency")] public int? AirDryFrequency { get; init; }
    [JsonPropertyName("collectDustOnSelfClean")] public bool? CollectDustOnSelfClean { get; init; }

    // Robot settings
    [JsonPropertyName("alarm")] public bool? Alarm { get; init; }
    [JsonPropertyName("volume")] public int? Volume { get; init; }
    [JsonPropertyName("voiceLanguage")] public string? VoiceLanguage { get; init; }
    [JsonPropertyName("childLock")] public bool? ChildLock { get; init; }
    /// <summary>"Prolonger les préparatifs de lavage". Present in the app's settings model; not seen in CURRENT-STATE yet.</summary>
    [JsonPropertyName("washMopBeforeClean")] public bool? WashMopBeforeClean { get; init; }

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; init; }

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static RobotState? Parse(string json)
    {
        try { return JsonSerializer.Deserialize<RobotState>(json, Options); }
        catch (JsonException) { return null; }
    }

    /// <summary>True for the periodic position-only message that carries no other state.</summary>
    public bool IsPositionOnly => State is null && GlobalPosition is { Count: > 0 };

    public bool IsDocked => State is "INACTIVE_CHARGING" or "INACTIVE_CHARGED" or "FULL_CLEAN_CHARGING";
    public bool IsCleaning => State is not null && State.StartsWith("FULL_CLEAN_", StringComparison.Ordinal) && State is not ("FULL_CLEAN_FINISHED" or "FULL_CLEAN_ABORTED" or "FULL_CLEAN_ABANDONED");
    public bool IsPaused => State == "FULL_CLEAN_PAUSED";
    /// <summary>Building a map: MAPPING_RUNNING, then MAPPING_FINISHED.</summary>
    public bool IsMapping => State is not null && State.StartsWith("MAPPING_", StringComparison.Ordinal) && State != "MAPPING_FINISHED";
    /// <summary>Observed as plain ABORTED (not FULL_CLEAN_ABORTED) right after an ABORT, with fault 2104.</summary>
    public bool IsAborted => State is "ABORTED" or "FULL_CLEAN_ABORTED" or "MAPPING_ABORTED";
    public bool IsDockBusy => DockState is not null and not "IDLE";

    /// <summary>Faults that need the user, excluding the status indicators.</summary>
    public IEnumerable<ActiveFault> RealFaults => (ActiveFaults ?? []).Where(f => !f.IsStatusIndicator);

    public RobotPosition? LatestPosition => GlobalPosition?.MaxBy(p => p.Id);

    /// <summary>
    /// Merges a newer message into this state. A full message replaces everything it carries; a
    /// position-only message updates the position and keeps the rest.
    /// </summary>
    public RobotState Merge(RobotState newer)
    {
        if (newer.IsPositionOnly)
            return this with { GlobalPosition = newer.GlobalPosition, Time = newer.Time ?? Time };
        return newer with { GlobalPosition = newer.GlobalPosition ?? GlobalPosition };
    }
}

/// <summary>
/// One jdm property set, from prop.get replies and prop.post pushes. Values are kept as JSON since
/// the dialect mixes numbers, strings, nulls and nested objects. Names are snake_case.
/// </summary>
public sealed class JdmProperties
{
    private readonly Dictionary<string, JsonNode?> _values = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, JsonNode?> Values => _values;

    public void Merge(JsonObject props)
    {
        foreach (var (k, v) in props)
            _values[k] = v?.DeepClone();
    }

    public int? GetInt(string name) =>
        _values.TryGetValue(name, out var n) && n is JsonValue v && v.TryGetValue<int>(out var i) ? i : null;

    public string? GetString(string name) =>
        _values.TryGetValue(name, out var n) && n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    /// <summary>0 vacuum only, 7 vacuum then mop (observed); other values not yet mapped.</summary>
    public int? SweepType => GetInt("sweep_type");
    public int? WorkMode => GetInt("work_mode");
    public int? Status => GetInt("status");
    public int? Fault => GetInt("fault");
    public int? Battery => GetInt("quantity");
    public long? CurrentMapId => _values.TryGetValue("current_map_id", out var n) && n is JsonValue v && v.TryGetValue<long>(out var l) ? l : null;
    /// <summary>Square decimetres cleaned so far in the current task, as pushed by prop.post.</summary>
    public int? CleaningArea => GetInt("cleaning_area");
    public int? CleaningTimeMinutes => GetInt("cleaning_time");

    /// <summary>What the dock is doing: 0 idle, 1 washing the roller, 2 drying it, 5 emptying the bin (observed values).</summary>
    public int? StationAct => GetInt("station_act");
    /// <summary>Charging on the dock; the jdm side of INACTIVE_CHARGING.</summary>
    public bool? ChargeState => GetBool("charge_state");
    /// <summary>
    /// Set from the moment the robot interrupts a clean to go and wash its roller until it is back
    /// on the dock, when station_act 1 takes over. The classic dialect only reports the washing
    /// itself (dockState WASHING_MOP), not the trip back.
    /// </summary>
    public bool? BackToWash => GetBool("back_to_wash");
    /// <summary>Countdown of the dock's current timed action, see <see cref="DockTimer"/>.</summary>
    public DockTimer? WorkTime =>
        _values.TryGetValue("work_time", out var n) && n is JsonObject o
        && IntOf(o["type"]) is { } type && IntOf(o["total"]) is { } total && IntOf(o["surplus"]) is { } surplus
            ? new DockTimer(type, total, surplus)
            : null;

    /// <summary>
    /// "order_total": how many schedules the active map has and how many are on, pushed after every
    /// schedule change and every change of active map.
    /// </summary>
    public ScheduleSummary? OrderTotal => ScheduleCommands.ParseSummary(_values.GetValueOrDefault("order_total"));

    private bool? GetBool(string name) => GetInt(name) is { } i ? i != 0 : null;

    private static int? IntOf(JsonNode? n) => n is JsonValue v && v.TryGetValue<int>(out var i) ? i : null;
}

/// <summary>
/// jdm "work_time": the dock's timed action, pushed once a minute while it runs and answered by
/// prop.get at any time. Only observed for mop drying: type 3, total 10 800 s for the 3 h setting,
/// surplus counting down in 60 s steps and left at 0 once done.
/// </summary>
public sealed record DockTimer(int Type, int TotalSeconds, int RemainingSeconds)
{
    public TimeSpan Remaining => TimeSpan.FromSeconds(Math.Max(0, RemainingSeconds));
}
