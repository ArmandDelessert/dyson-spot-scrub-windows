using System.Globalization;
using System.Text.Json.Nodes;

namespace DyssCockpit.Core;

/// <summary>What the robot does in a room. Names follow the REST metadata (settings.cleanType).</summary>
public enum CleanType { Vacuum, Mop, VacuumAndMop, VacuumThenMop }

public static class CleanTypes
{
    /// <summary>REST value. "vacuum" and "vacuumAndMop" observed; the other two follow the APK's naming.</summary>
    public static string ToRest(this CleanType t) => t switch
    {
        CleanType.Vacuum => "vacuum",
        CleanType.Mop => "mop",
        CleanType.VacuumAndMop => "vacuumAndMop",
        CleanType.VacuumThenMop => "vacuumThenMop",
        _ => "vacuum",
    };

    public static CleanType FromRest(string? s) => s switch
    {
        "mop" => CleanType.Mop,
        "vacuumAndMop" => CleanType.VacuumAndMop,
        "vacuumThenMop" => CleanType.VacuumThenMop,
        _ => CleanType.Vacuum,
    };

    /// <summary>
    /// Value of index 3 of a jdm room_preference entry. 0 and 1 are confirmed by captures
    /// (vacuum, vacuum and mop); 2 and 3 come from the ha-dyson-spot-scrub project.
    /// </summary>
    public static int ToJdm(this CleanType t) => t switch
    {
        CleanType.Vacuum => 0,
        CleanType.VacuumAndMop => 1,
        CleanType.Mop => 2,
        CleanType.VacuumThenMop => 3,
        _ => 0,
    };

    public static CleanType FromJdm(int v) => v switch
    {
        1 => CleanType.VacuumAndMop,
        2 => CleanType.Mop,
        3 => CleanType.VacuumThenMop,
        _ => CleanType.Vacuum,
    };
}

/// <summary>Vacuum power. REST value of settings.cleaningStrategy. Confirmed value: "auto".</summary>
public enum CleaningStrategy { Auto, Quick, Quiet, Boost }

public static class CleaningStrategies
{
    public static string ToRest(this CleaningStrategy s) => s switch
    {
        CleaningStrategy.Quick => "quick",
        CleaningStrategy.Quiet => "quiet",
        CleaningStrategy.Boost => "boost",
        _ => "auto",
    };

    public static CleaningStrategy FromRest(string? s) => s switch
    {
        "quick" => CleaningStrategy.Quick,
        "quiet" => CleaningStrategy.Quiet,
        "boost" => CleaningStrategy.Boost,
        _ => CleaningStrategy.Auto,
    };

    /// <summary>Value of index 4 of a jdm room_preference entry, all four confirmed by the schedules captured on 2026-09-26.</summary>
    public static int ToJdm(this CleaningStrategy s) => s switch
    {
        CleaningStrategy.Boost => 1,
        CleaningStrategy.Quiet => 2,
        CleaningStrategy.Quick => 3,
        _ => 0,
    };

    public static CleaningStrategy FromJdm(int v) => v switch
    {
        1 => CleaningStrategy.Boost,
        2 => CleaningStrategy.Quiet,
        3 => CleaningStrategy.Quick,
        _ => CleaningStrategy.Auto,
    };
}

/// <summary>
/// Mop water level. REST value of settings.waterLevel. Confirmed value: "low". The app also has a
/// "very low" level not offered on this screen, so it is not modelled here.
/// </summary>
public enum WaterLevel { Low, Medium, High }

public static class WaterLevels
{
    public static string ToRest(this WaterLevel w) => w switch
    {
        WaterLevel.Medium => "medium",
        WaterLevel.High => "high",
        _ => "low",
    };

    public static WaterLevel FromRest(string? s) => s switch
    {
        "medium" => WaterLevel.Medium,
        "high" => WaterLevel.High,
        _ => WaterLevel.Low,
    };

    /// <summary>Value of index 5 of a jdm room_preference entry, confirmed by the schedules captured on 2026-09-26.</summary>
    public static int ToJdm(this WaterLevel w) => w switch
    {
        WaterLevel.Medium => 1,
        WaterLevel.High => 2,
        _ => 0,
    };

    public static WaterLevel FromJdm(int v) => v switch
    {
        1 => WaterLevel.Medium,
        2 => WaterLevel.High,
        _ => WaterLevel.Low,
    };
}

/// <summary>
/// How one room is cleaned: the four choices of the phone app's per-room screen. The robot takes
/// them as indices 3 to 6 of a room_preference entry (see <see cref="WriteTo"/>).
/// </summary>
public sealed record RoomSettings(CleanType CleanType, CleaningStrategy Strategy = CleaningStrategy.Auto, WaterLevel Water = WaterLevel.Low, int MopPasses = 1)
{
    public const int IndexCleanType = 3;
    public const int IndexStrategy = 4;
    public const int IndexWater = 5;
    public const int IndexMopPasses = 6;

    public bool Vacuums => CleanType is not CleanType.Mop;
    public bool Mops => CleanType is not CleanType.Vacuum;

    /// <summary>
    /// Writes indices 3 to 6. A setting that does not apply to the clean type goes out as 0, as the
    /// phone sends it: a room that is only mopped has no vacuum power, one only vacuumed no water
    /// level or passes. Passes are 0 for one, 1 for two.
    /// </summary>
    public void WriteTo(JsonArray entry)
    {
        entry[IndexCleanType] = CleanType.ToJdm();
        entry[IndexStrategy] = Vacuums ? Strategy.ToJdm() : 0;
        entry[IndexWater] = Mops ? Water.ToJdm() : 0;
        entry[IndexMopPasses] = Mops && MopPasses >= 2 ? 1 : 0;
    }

    /// <summary>Reads indices 3 to 6 back; missing or non-numeric values count as 0.</summary>
    public static RoomSettings ReadFrom(JsonArray entry)
    {
        int At(int i) => i < entry.Count && entry[i] is JsonValue v && v.TryGetValue<int>(out var n) ? n : 0;
        return new RoomSettings(CleanTypes.FromJdm(At(IndexCleanType)), CleaningStrategies.FromJdm(At(IndexStrategy)),
            WaterLevels.FromJdm(At(IndexWater)), At(IndexMopPasses) >= 1 ? 2 : 1);
    }
}

/// <summary>One room of a clean request: which zone, how, in which order.</summary>
public sealed record RoomSelection(string ZoneId, RoomSettings Settings, int Order);

/// <summary>
/// The start sequence of the official app, reproduced from captures. Four messages on two topics:
/// preferences, START, current map, room list. The room_preference arrays are taken from the
/// robot's own get_preference so unknown indices stay untouched; only the room settings (indices 3
/// to 6, see <see cref="RoomSettings"/>), the selection flag (index 8) and the order (index 10) of
/// the chosen rooms are rewritten, as the phone does.
/// </summary>
public static class CleaningSequence
{
    public const int IndexSelected = 8;
    public const int IndexOrder = 10;

    public static async Task StartAsync(IRobotCommands robot, long mapId, IReadOnlyList<RoomSelection> rooms, CancellationToken ct = default)
    {
        if (rooms.Count == 0) throw new ArgumentException("At least one room is required.", nameof(rooms));

        var prefs = await robot.RequestJdmAsync("service.get_preference", new JsonObject { ["map_id"] = mapId }, ct: ct).ConfigureAwait(false);
        var data = prefs["data"] as JsonObject;
        var roomArray = data?["room"] as JsonArray ?? new JsonArray();
        var uvSwitch = data?["uv_switch"]?.DeepClone() as JsonArray ?? new JsonArray();

        var bySelection = rooms.ToDictionary(r => r.ZoneId, r => r);
        var rewritten = new JsonArray();
        foreach (var node in roomArray)
        {
            if (node is not JsonArray entry || entry.Count < 2) continue;
            // The app publishes eleven elements while replies carry twelve.
            var arr = new JsonArray();
            for (var i = 0; i < Math.Min(11, entry.Count); i++) arr.Add(entry[i]?.DeepClone());
            while (arr.Count < 11) arr.Add(0);

            // Read as the robot may send it: a number of any width, or its text.
            var id = arr[0] is JsonValue v && v.TryGetValue<long>(out var n) ? n.ToString(CultureInfo.InvariantCulture)
                : arr[0] is JsonValue s && s.TryGetValue<string>(out var text) ? text
                : "";
            if (bySelection.TryGetValue(id, out var sel))
            {
                sel.Settings.WriteTo(arr);
                arr[IndexSelected] = 1;
                arr[IndexOrder] = sel.Order;
            }
            else
            {
                arr[IndexSelected] = 0;
            }
            rewritten.Add(arr);
        }

        await robot.SetRoomPreferenceAsync(mapId, rewritten, uvSwitch, ct).ConfigureAwait(false);
        await robot.StartZoneCleanAsync(mapId.ToString(CultureInfo.InvariantCulture), rooms.OrderBy(r => r.Order).Select(r => r.ZoneId), ct).ConfigureAwait(false);
        await robot.SetCurrentMapAsync(mapId, ct).ConfigureAwait(false);
        await robot.SetRoomCleanAsync(rooms.OrderBy(r => r.Order).Select(r => int.Parse(r.ZoneId, CultureInfo.InvariantCulture)), ct: ct).ConfigureAwait(false);
    }
}

/// <summary>
/// Cleaning a rectangle drawn on the map rather than whole rooms, the phone's "zone" clean. Three
/// messages, as captured on 2026-09-30: the classic START in spotZoneConfigured mode with the
/// rectangle and its settings, set_cur_map, then service.set_areas_start with the same corners and
/// the settings coded like a room's. The robot starts on the START; set_areas_start answered
/// result 1 two seconds later in the capture, the clean being under way already.
/// </summary>
public static class SpotCleanSequence
{
    /// <summary>
    /// The four corners of the upright rectangle spanned by two opposite corners, in the order the
    /// phone sends a spot zone: top-right, top-left, bottom-left, bottom-right (y pointing up).
    /// </summary>
    public static Point[] Corners(Point a, Point b)
    {
        double left = Math.Min(a.X, b.X), right = Math.Max(a.X, b.X), bottom = Math.Min(a.Y, b.Y), top = Math.Max(a.Y, b.Y);
        return [new(right, top), new(left, top), new(left, bottom), new(right, bottom)];
    }

    public static async Task StartAsync(IRobotCommands robot, long mapId, IReadOnlyList<Point> corners, RoomSettings settings, string? zoneId = null, CancellationToken ct = default)
    {
        if (corners.Count != 4) throw new ArgumentException("A zone has exactly four corners.", nameof(corners));

        var points = new JsonArray();
        foreach (var p in corners) points.Add(new JsonObject { ["x"] = p.X, ["y"] = p.Y });
        await robot.PublishCommandAsync(new JsonObject
        {
            ["cleaningProgramme"] = new JsonObject
            {
                ["persistentMapId"] = mapId.ToString(CultureInfo.InvariantCulture),
                ["spotZones"] = new JsonArray(new JsonObject
                {
                    ["id"] = zoneId ?? Guid.NewGuid().ToString(),
                    ["points"] = points,
                }),
                ["defaultSpotZoneSettings"] = new JsonObject
                {
                    ["cleaningStrategy"] = settings.Strategy.ToRest(),
                    ["cleanType"] = settings.CleanType.ToRest(),
                    ["waterLevel"] = settings.Water.ToRest(),
                    ["mopPasses"] = settings.MopPasses,
                    ["dryPasses"] = 1,
                },
            },
            ["cleaningMode"] = "spotZoneConfigured",
            ["fullCleanType"] = "immediate",
            ["mode-reason"] = "RAPP",
            ["msg"] = "START",
        }, ct).ConfigureAwait(false);

        await robot.SetCurrentMapAsync(mapId, ct).ConfigureAwait(false);

        // The same room-setting codes as room_preference (indices 3 to 6), zeroed where they do not apply.
        var coded = new JsonArray(0, 0, 0, 0, 0, 0, 0);
        settings.WriteTo(coded);
        var flat = new JsonArray();
        foreach (var p in corners) { flat.Add(p.X); flat.Add(p.Y); }
        await robot.PublishJdmAsync("service.set_areas_start", new JsonObject
        {
            ["ctrl_value"] = 1,
            ["zone_points"] = new JsonArray(flat),
            ["mode"] = coded[RoomSettings.IndexCleanType]!.GetValue<int>(),
            ["wind"] = coded[RoomSettings.IndexStrategy]!.GetValue<int>(),
            ["water"] = coded[RoomSettings.IndexWater]!.GetValue<int>(),
            ["clean_count"] = coded[RoomSettings.IndexMopPasses]!.GetValue<int>(),
            ["dry_clean_count"] = 0,
            ["action"] = 0,
            ["uv_switch"] = 0,
        }, ct).ConfigureAwait(false);
    }
}
