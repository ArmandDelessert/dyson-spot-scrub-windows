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

    /// <summary>
    /// The same set_cur_map, waiting for the robot's {"result": 0} rather than firing and
    /// forgetting as the start sequence does — for when the change is the whole point and the user
    /// should hear whether it took.
    /// </summary>
    public static async Task<MapEditResult?> ActivateMapAsync(this IRobotCommands robot, long mapId, CancellationToken ct = default) =>
        MapEditResult.From(await robot.RequestJdmAsync("service.set_cur_map", new JsonObject { ["map_id"] = mapId }, ct: ct).ConfigureAwait(false));

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

    // ---- Map editing ----------------------------------------------------------
    //
    // All four were captured on 2026-09-19 while the official app edited a real map. They answer
    // with {map_id, map_type, timestamp}: the robot has re-saved the map, and a MAP-UPLOAD-STATUS
    // follows once the cloud copy has caught up, which is what a caller should wait for before
    // re-reading the map over REST. "lang" is 5 for French, as the app sends.

    /// <summary>
    /// Deletes a map for good, with everything attached to it. Captured on 2026-09-23 from the phone
    /// app, on the map that was active at the time: the robot accepted it and made another map
    /// active on its own (the next MAP-UPLOAD-STATUS names that one, not the deleted one).
    /// </summary>
    public static async Task<MapEditResult?> DeleteMapAsync(this IRobotCommands robot, long mapId, CancellationToken ct = default) =>
        MapEditResult.From(await robot.RequestJdmAsync("service.del_map", new JsonObject { ["map_id"] = mapId }, ct: ct).ConfigureAwait(false));

    /// <summary>Renames a map. The name is a plain string, unlike a room's (see <see cref="RenameRoomAsync"/>).</summary>
    public static async Task<MapEditResult?> RenameMapAsync(this IRobotCommands robot, long mapId, string name, CancellationToken ct = default) =>
        MapEditResult.From(await robot.RequestJdmAsync("service.rename_map", new JsonObject
        {
            ["map_id"] = mapId,
            ["map_name"] = name,
        }, ct: ct).ConfigureAwait(false));

    /// <summary>
    /// Renames a room. The wire field is never a bare string: the app always sends the JSON form
    /// {"type": "...", "name": "..."}, with type "custom" for a name the user made up (both forms
    /// captured). See <see cref="RoomPreference.JoinName"/>.
    /// </summary>
    public static async Task<MapEditResult?> RenameRoomAsync(this IRobotCommands robot, long mapId, int roomId, string name, string type = "custom", CancellationToken ct = default) =>
        MapEditResult.From(await robot.RequestJdmAsync("service.rename_room", new JsonObject
        {
            ["map_id"] = mapId,
            ["room_id"] = roomId,
            ["room_name"] = RoomPreference.JoinName(name, type),
        }, ct: ct).ConfigureAwait(false));

    /// <summary>Merges rooms of a map into one; the first id given is the one that survives.</summary>
    public static async Task<MapEditResult?> MergeRoomsAsync(this IRobotCommands robot, long mapId, IEnumerable<int> roomIds, int lang = 5, CancellationToken ct = default)
    {
        var ids = new JsonArray();
        foreach (var id in roomIds) ids.Add(id);
        return MapEditResult.From(await robot.RequestJdmAsync("service.arrange_room", new JsonObject
        {
            ["map_id"] = mapId,
            ["room_ids"] = ids,
            ["lang"] = lang,
        }, ct: ct).ConfigureAwait(false));
    }

    /// <summary>
    /// Cuts a room in two along the straight line between two world points. The robot decides where
    /// the new boundary actually lands: it snaps the cut to its own occupancy grid, so the result
    /// rarely matches the line exactly. Only a straight cut exists — no command sets a room's
    /// outline, which is why a room cannot be given an arbitrary shape.
    /// </summary>
    public static async Task<MapEditResult?> SplitRoomAsync(this IRobotCommands robot, long mapId, int roomId, Point from, Point to, int lang = 5, CancellationToken ct = default) =>
        MapEditResult.From(await robot.RequestJdmAsync("service.split_room", new JsonObject
        {
            ["map_id"] = mapId,
            ["room_id"] = roomId,
            ["split_points"] = new JsonArray(from.X, from.Y, to.X, to.Y),
            ["lang"] = lang,
        }, ct: ct).ConfigureAwait(false));
}
