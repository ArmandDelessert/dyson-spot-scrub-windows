using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Dyss.Core;

/// <summary>The days a schedule runs on: jdm "day", one bit per day, Monday first (confirmed on 2026-09-26).</summary>
[Flags]
public enum ScheduleDays
{
    None = 0,
    Monday = 1,
    Tuesday = 2,
    Wednesday = 4,
    Thursday = 8,
    Friday = 16,
    Saturday = 32,
    Sunday = 64,
    Weekdays = Monday | Tuesday | Wednesday | Thursday | Friday,
    Weekend = Saturday | Sunday,
    All = Weekdays | Weekend,
}

/// <summary>A room a schedule cleans, and how.</summary>
public sealed record ScheduledRoom(int ZoneId, RoomSettings Settings);

/// <summary>A room of the map, as every schedule has to list all of them.</summary>
public sealed record MapRoom(int ZoneId, string Name);

/// <summary>
/// One scheduled clean. The robot keeps it with its map and never gives it back in detail — get_order
/// only answers a count — so whoever creates a schedule has to remember it. <see cref="Rooms"/> are
/// the rooms to clean, in cleaning order.
/// </summary>
public sealed record CleaningSchedule(
    long Id,
    long MapId,
    bool Enabled,
    ScheduleDays Days,
    int Hour,
    int Minute,
    IReadOnlyList<ScheduledRoom> Rooms)
{
    /// <summary>
    /// The add_order parameters, shaped exactly as the phone sends them (captures of 2026-09-25 and
    /// 26): every room of the map, those to clean first in their order counted from 0, the others
    /// after with everything at 0; names as plain strings; twelve elements each; uv_switch 0 for a
    /// room that is mopped and 1 otherwise. Rooms of the schedule the map no longer has are left out.
    /// </summary>
    public JsonObject ToParams(IReadOnlyList<MapRoom> mapRooms, int timeZoneSeconds)
    {
        var names = mapRooms.ToDictionary(r => r.ZoneId, r => r.Name);
        var chosen = Rooms.Where(r => names.ContainsKey(r.ZoneId)).ToList();
        var chosenIds = chosen.Select(r => r.ZoneId).ToHashSet();

        var preference = new JsonArray();
        var uvSwitch = new JsonArray();
        var order = 0;
        foreach (var room in chosen)
        {
            var entry = Entry(room.ZoneId, names[room.ZoneId], selected: true, order++);
            room.Settings.WriteTo(entry);
            preference.Add(entry);
            uvSwitch.Add(new JsonArray(room.ZoneId, room.Settings.Mops ? 0 : 1));
        }
        foreach (var room in mapRooms.Where(r => !chosenIds.Contains(r.ZoneId)))
        {
            preference.Add(Entry(room.ZoneId, room.Name, selected: false, order++));
            uvSwitch.Add(new JsonArray(room.ZoneId, 1));
        }

        return new JsonObject
        {
            ["id"] = Id,
            ["enable"] = Enabled ? 1 : 0,
            ["day"] = (int)Days,
            ["hour"] = Hour,
            ["minute"] = Minute,
            ["repeat"] = 1,
            ["map_id"] = MapId,
            ["room_count"] = preference.Count,
            ["time_zone"] = timeZoneSeconds,
            ["prefer_type"] = 1,
            ["is_global"] = 0,
            ["areas"] = new JsonArray(),
            ["room_preference"] = preference,
            ["uv_switch"] = uvSwitch,
        };
    }

    private static JsonArray Entry(int zoneId, string name, bool selected, int order) =>
        [zoneId, name, 0, 0, 0, 0, 0, 0, selected ? 1 : 0, 0, order, 0];

    /// <summary>
    /// Reads an add_order's parameters back, for a schedule seen in a capture. Rooms flagged at
    /// index 8 are the ones cleaned, taken in the order of index 10.
    /// </summary>
    public static CleaningSchedule? FromParams(JsonObject p)
    {
        if (Long(p["id"]) is not { } id || Long(p["map_id"]) is not { } mapId) return null;
        var rooms = new List<(int Order, ScheduledRoom Room)>();
        foreach (var node in p["room_preference"] as JsonArray ?? [])
        {
            if (node is not JsonArray e || e.Count < 11 || Long(e[0]) is not { } zone || Long(e[CleaningSequence.IndexSelected]) != 1) continue;
            rooms.Add(((int)(Long(e[CleaningSequence.IndexOrder]) ?? 0), new ScheduledRoom((int)zone, RoomSettings.ReadFrom(e))));
        }
        return new CleaningSchedule(id, mapId, Long(p["enable"]) == 1, (ScheduleDays)(Long(p["day"]) ?? 0),
            (int)(Long(p["hour"]) ?? 0), (int)(Long(p["minute"]) ?? 0),
            rooms.OrderBy(r => r.Order).Select(r => r.Room).ToList());
    }

    private static long? Long(JsonNode? n) => n is JsonValue v && v.TryGetValue<long>(out var l) ? l : null;

    /// <summary>A fresh id: the robot takes whatever the client picks, the phone a random positive 32-bit number.</summary>
    public static long NewId() => RandomNumberGenerator.GetInt32(1, int.MaxValue);

    /// <summary>
    /// The time_zone field: the offset in seconds of the robot's time zone at the given moment. The
    /// phone derives it from the zone the cloud holds for the robot (Europe/London, 3600 in summer,
    /// on the captures' account). Null when the zone is unknown to this machine.
    /// </summary>
    public static int? TimeZoneOffsetSeconds(string? ianaTimeZone, DateTimeOffset at)
    {
        if (string.IsNullOrWhiteSpace(ianaTimeZone)) return null;
        try
        {
            return (int)TimeZoneInfo.FindSystemTimeZoneById(ianaTimeZone).GetUtcOffset(at).TotalSeconds;
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return null;
        }
    }
}

// ---- The cloud's copy ---------------------------------------------------------------------

/// <summary>
/// GET /v1/unifiedscheduler/{serial}/events?productType=804: the schedules of the robot's active
/// map, as the phone reads them. The robot answers get_order with a count only; the cloud keeps the
/// detail, whoever created the schedule (phone or this app, whose add_order it picks up), and
/// swaps the list when the active map changes. Read off the APK and checked on 2026-09-28.
/// </summary>
public sealed record ScheduleEvents(
    [property: JsonPropertyName("serial")] string? Serial,
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("events")] List<ScheduleEvent>? Events);

/// <param name="GroupId">The schedule's id, the same the robot's add_order and del_order use.</param>
/// <param name="Days">0 Sunday, 1 Monday … 6 Saturday.</param>
/// <param name="StartTime">"HH:mm".</param>
public sealed record ScheduleEvent(
    [property: JsonPropertyName("groupId")] long GroupId,
    [property: JsonPropertyName("days")] List<int>? Days,
    [property: JsonPropertyName("startTime")] string? StartTime,
    [property: JsonPropertyName("weeklyRepeat")] bool WeeklyRepeat,
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("settings")] ScheduleEventSettings? Settings)
{
    /// <summary>
    /// The same schedule in this app's terms, or null when it cannot be read (no map, no time).
    /// The rooms kept are those the event marks selected, in its order; their settings are the REST
    /// names of the per-room choices.
    /// </summary>
    public CleaningSchedule? ToSchedule()
    {
        if (Settings is null || !long.TryParse(Settings.PersistentMapId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var mapId)) return null;
        if (!TimeOnly.TryParseExact(StartTime, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)) return null;
        var days = (Days ?? []).Aggregate(ScheduleDays.None, (all, d) => all | DayFromCloud(d));
        var rooms = (Settings.Zones ?? [])
            .Where(z => z.IsSelected == true && int.TryParse(z.Id, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            .OrderBy(z => z.Order ?? 0)
            .Select(z => new ScheduledRoom(int.Parse(z.Id, CultureInfo.InvariantCulture), new RoomSettings(
                CleanTypes.FromRest(z.Settings?.CleanType),
                CleaningStrategies.FromRest(z.Settings?.CleaningStrategy),
                WaterLevels.FromRest(z.Settings?.WaterLevel),
                z.Settings?.MopPasses is >= 2 ? 2 : 1)))
            .ToList();
        return new CleaningSchedule(GroupId, mapId, Enabled, days, time.Hour, time.Minute, rooms);
    }

    /// <summary>The cloud counts from Sunday = 0, the robot's bits from Monday.</summary>
    public static ScheduleDays DayFromCloud(int day) => day switch
    {
        0 => ScheduleDays.Sunday,
        >= 1 and <= 6 => (ScheduleDays)(1 << (day - 1)),
        _ => ScheduleDays.None,
    };
}

/// <summary>The RB05's own part of an event: its map and every room of it, as the map metadata lists them.</summary>
public sealed record ScheduleEventSettings(
    [property: JsonPropertyName("persistentMapId")] string? PersistentMapId,
    [property: JsonPropertyName("zones")] List<ZoneMetadata>? Zones);

/// <summary>get_order's whole answer: how many schedules the active map has, how many are on, and a fingerprint of them.</summary>
public sealed record ScheduleSummary(int Total, int Enabled, string? Md5);

public static class ScheduleCommands
{
    /// <summary>Creates a schedule, or replaces the one with the same id: add_order does both. True when the robot answered result 0.</summary>
    public static async Task<bool> SaveScheduleAsync(this IRobotCommands robot, CleaningSchedule schedule, IReadOnlyList<MapRoom> mapRooms, int timeZoneSeconds, CancellationToken ct = default) =>
        MapEditResult.From(await robot.RequestJdmAsync("service.add_order", schedule.ToParams(mapRooms, timeZoneSeconds), ct: ct).ConfigureAwait(false)) is not null;

    public static async Task<bool> DeleteScheduleAsync(this IRobotCommands robot, long id, CancellationToken ct = default) =>
        MapEditResult.From(await robot.RequestJdmAsync("service.del_order", new JsonObject { ["id"] = id }, ct: ct).ConfigureAwait(false)) is not null;

    /// <summary>The count of schedules on the active map; the robot has nothing more detailed to give.</summary>
    public static async Task<ScheduleSummary?> GetScheduleSummaryAsync(this IRobotCommands robot, CancellationToken ct = default) =>
        ParseSummary((await robot.RequestJdmAsync("service.get_order", new JsonObject(), ct: ct).ConfigureAwait(false))["data"]?["order_data_lite"]);

    /// <summary>Reads get_order's order_data_lite, or the order_total the robot pushes after each change (which has no md5).</summary>
    public static ScheduleSummary? ParseSummary(JsonNode? node)
    {
        if (node is not JsonObject o) return null;
        int? Int(string name) => o[name] is JsonValue v && v.TryGetValue<int>(out var i) ? i : null;
        if (Int("total") is not { } total) return null;
        return new ScheduleSummary(total, Int("enable") ?? 0, o["md5"] is JsonValue m && m.TryGetValue<string>(out var s) ? s : null);
    }
}

/// <summary>
/// Rough length of a clean, to warn when one schedule would still be running when the next is due —
/// the robot then skips the second, as the phone app warns. The rate comes from the account's own
/// history when there is enough of it.
/// </summary>
public static class CleanDurationEstimate
{
    /// <summary>Used until the history has three cleans to learn from: the captures' robot took 128 minutes for 87 m², 7 for 4.7 m².</summary>
    public const double DefaultMinutesPerSquareMetre = 1.5;

    /// <summary>The median minutes per square metre of past cleans, ignoring ones too small to say anything.</summary>
    public static double MinutesPerSquareMetre(IEnumerable<CleanSummary> history)
    {
        var rates = history
            .Where(c => c.CleanDurationMinutes is > 0 && c.AreaCleanedSquareMetres is >= 2)
            .Select(c => c.CleanDurationMinutes!.Value / c.AreaCleanedSquareMetres!.Value)
            .Order()
            .ToList();
        return rates.Count < 3 ? DefaultMinutesPerSquareMetre : rates[rates.Count / 2];
    }

    /// <summary>Minutes for the given rooms: vacuum then mop is two runs over the floor, a second mop pass half a run more.</summary>
    public static int Minutes(IEnumerable<(double Area, RoomSettings Settings)> rooms, double minutesPerSquareMetre) =>
        (int)Math.Ceiling(rooms.Sum(r =>
        {
            var factor = r.Settings.CleanType == CleanType.VacuumThenMop ? 2.0 : 1.0;
            if (r.Settings.Mops && r.Settings.MopPasses >= 2) factor += 0.5;
            return r.Area * minutesPerSquareMetre * factor;
        }));

    /// <summary>
    /// Whether <paramref name="later"/> falls due while <paramref name="earlier"/> is still running,
    /// on any day they share — including a clean started late on Sunday and running past midnight.
    /// </summary>
    public static bool Overlaps(CleaningSchedule earlier, int earlierMinutes, CleaningSchedule later)
    {
        const int Week = 7 * 24 * 60;
        foreach (var a in Starts(earlier))
            foreach (var b in Starts(later))
            {
                var gap = ((b - a) % Week + Week) % Week;
                if (gap < earlierMinutes) return true;
            }
        return false;
    }

    private static IEnumerable<int> Starts(CleaningSchedule s)
    {
        for (var day = 0; day < 7; day++)
            if (((int)s.Days & (1 << day)) != 0) yield return day * 24 * 60 + s.Hour * 60 + s.Minute;
    }
}

/// <summary>Short French names for the days, and a compact text for a set of them.</summary>
public static class ScheduleDayLabels
{
    public static readonly IReadOnlyList<(ScheduleDays Day, string Letter, string Short, string Long)> All =
    [
        (ScheduleDays.Monday, "L", "lun.", "lundi"),
        (ScheduleDays.Tuesday, "M", "mar.", "mardi"),
        (ScheduleDays.Wednesday, "M", "mer.", "mercredi"),
        (ScheduleDays.Thursday, "J", "jeu.", "jeudi"),
        (ScheduleDays.Friday, "V", "ven.", "vendredi"),
        (ScheduleDays.Saturday, "S", "sam.", "samedi"),
        (ScheduleDays.Sunday, "D", "dim.", "dimanche"),
    ];

    public static string Describe(ScheduleDays days) => days switch
    {
        ScheduleDays.None => "aucun jour",
        ScheduleDays.All => "tous les jours",
        ScheduleDays.Weekdays => "du lundi au vendredi",
        ScheduleDays.Weekend => "le week-end",
        _ when All.Count(d => days.HasFlag(d.Day)) == 1 => "le " + All.First(d => days.HasFlag(d.Day)).Long,
        _ => string.Join(", ", All.Where(d => days.HasFlag(d.Day)).Select(d => d.Short)),
    };

    public static string Time(int hour, int minute) => string.Create(CultureInfo.InvariantCulture, $"{hour:00}:{minute:00}");
}

