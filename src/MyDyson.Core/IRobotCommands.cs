using System.Text.Json.Nodes;

namespace MyDyson.Core;

/// <summary>
/// The wires into the robot that command sequences are written against: a jdm request that
/// waits for its reply, and fire-and-forget publishes on the jdm and classic topics.
/// <see cref="RobotMqttClient"/> is the real one; tests substitute a recorder to check the exact
/// payloads a sequence produces without a broker.
/// </summary>
public interface IRobotCommands
{
    /// <summary>Publishes a jdm request and returns the reply carrying the same msgId.</summary>
    Task<JsonObject> RequestJdmAsync(string method, JsonObject? parameters = null, TimeSpan? timeout = null, CancellationToken ct = default);

    /// <summary>Publishes a jdm request ({"method": "service.x", "params": {...}}) without waiting.</summary>
    Task PublishJdmAsync(string method, JsonObject? parameters = null, CancellationToken ct = default);

    /// <summary>Publishes a classic Dyson command ({"msg": ..., ...}); the sender fills in "time".</summary>
    Task PublishCommandAsync(JsonObject payload, CancellationToken ct = default);
}

/// <summary>
/// The four messages of the official app's start sequence, as observed on 2026-09-19. Written
/// against <see cref="IRobotCommands"/> so <see cref="CleaningSequence"/> can be tested payload
/// by payload; the other captured commands live on <see cref="RobotMqttClient"/>.
/// </summary>
public static class RobotCommands
{
    /// <summary>
    /// Sets per-room settings. Rooms are positional arrays, not objects: index 0 is the zone id and
    /// index 1 its name; the rest carry per-room settings and the cleaning order.
    /// </summary>
    public static Task SetRoomPreferenceAsync(this IRobotCommands robot, long mapId, JsonArray roomPreference, JsonArray? uvSwitch = null, CancellationToken ct = default) =>
        robot.PublishJdmAsync("service.set_preference", new JsonObject
        {
            ["map_id"] = mapId,
            ["prefer_type"] = 1,
            ["room_preference"] = roomPreference,
            ["uv_switch"] = uvSwitch ?? new JsonArray(),
        }, ct);

    /// <summary>
    /// Starts a clean of the given zones of a map, which is what the app's start button does.
    /// The app sends the room preferences first with <see cref="SetRoomPreferenceAsync"/>, then this
    /// START, then confirms with <see cref="SetCurrentMapAsync"/> and <see cref="SetRoomCleanAsync"/>.
    /// </summary>
    public static Task StartZoneCleanAsync(this IRobotCommands robot, string persistentMapId, IEnumerable<string> zoneIds, CancellationToken ct = default)
    {
        var zones = new JsonArray();
        foreach (var z in zoneIds) zones.Add(z);
        return robot.PublishCommandAsync(new JsonObject
        {
            ["msg"] = "START",
            ["mode-reason"] = "RAPP",
            ["cleaningMode"] = "zoneConfigured",
            ["fullCleanType"] = "immediate",
            ["cleaningProgramme"] = new JsonObject
            {
                ["persistentMapId"] = persistentMapId,
                ["zonesDefinitionLastUpdatedDate"] = "",
                ["unorderedZones"] = zones,
            },
        }, ct);
    }

    public static Task SetCurrentMapAsync(this IRobotCommands robot, long mapId, CancellationToken ct = default) =>
        robot.PublishJdmAsync("service.set_cur_map", new JsonObject { ["map_id"] = mapId }, ct);

    public static Task SetRoomCleanAsync(this IRobotCommands robot, IEnumerable<int> roomIds, int cleanType = 0, int ctrlValue = 1, CancellationToken ct = default)
    {
        var ids = new JsonArray();
        foreach (var id in roomIds) ids.Add(id);
        return robot.PublishJdmAsync("service.set_room_clean", new JsonObject
        {
            ["ctrl_value"] = ctrlValue,
            ["clean_type"] = cleanType,
            ["room_ids"] = ids,
        }, ct);
    }
}
