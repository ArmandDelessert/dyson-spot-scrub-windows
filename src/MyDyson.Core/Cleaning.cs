using System.Globalization;
using System.Text.Json.Nodes;

namespace MyDyson.Core;

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
}

/// <summary>One room of a clean request: which zone, how, in which order.</summary>
public sealed record RoomSelection(string ZoneId, CleanType CleanType, int Order);

/// <summary>
/// The start sequence of the official app, reproduced from captures. Four messages on two topics:
/// preferences, START, current map, room list. The room_preference arrays are taken from the
/// robot's own get_preference so unknown indices stay untouched; only the clean type (index 3),
/// the selection flag (index 8) and the order (index 10) are rewritten.
/// </summary>
public static class CleaningSequence
{
    public const int IndexCleanType = 3;
    public const int IndexSelected = 8;
    public const int IndexOrder = 10;

    public static async Task StartAsync(RobotMqttClient robot, long mapId, IReadOnlyList<RoomSelection> rooms, CancellationToken ct = default)
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

            var id = arr[0]?.GetValue<int>().ToString(CultureInfo.InvariantCulture) ?? "";
            if (bySelection.TryGetValue(id, out var sel))
            {
                arr[IndexCleanType] = sel.CleanType.ToJdm();
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
