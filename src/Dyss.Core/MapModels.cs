using System.Text.Json.Nodes;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dyss.Core;

// Models for the map endpoints of appapi.cp.dyson.com, as returned for an RB05 in September 2026.
// Coordinates are metres relative to the dock. Every list is optional: the server omits empty ones.

/// <param name="Update">Only meaningful on a cleanPath point: 0 driving, 1 actively cleaning. Null elsewhere.</param>
public sealed record Point(
    [property: JsonPropertyName("x")] double X,
    [property: JsonPropertyName("y")] double Y,
    [property: JsonPropertyName("update")] int? Update = null);

public sealed record DockLocation(
    [property: JsonPropertyName("x")] double X,
    [property: JsonPropertyName("y")] double Y,
    [property: JsonPropertyName("angle")] double Angle);

/// <summary>Per-zone cleaning settings, as edited in the app and sent back with the zone selection.</summary>
public sealed record ZoneSettings(
    [property: JsonPropertyName("cleaningStrategy")] string? CleaningStrategy,
    [property: JsonPropertyName("cleanType")] string? CleanType,
    [property: JsonPropertyName("waterLevel")] string? WaterLevel,
    [property: JsonPropertyName("mopPasses")] int? MopPasses,
    [property: JsonPropertyName("dryPasses")] int? DryPasses,
    [property: JsonPropertyName("isUvScanOn")] bool? IsUvScanOn);

/// <summary>A zone as listed by GET /v2/app/{serial}/persistent-map-metadata.</summary>
public sealed record ZoneMetadata(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("nameLocation")] Point? NameLocation,
    [property: JsonPropertyName("isSelected")] bool? IsSelected,
    [property: JsonPropertyName("order")] int? Order,
    [property: JsonPropertyName("area")] double? Area,
    [property: JsonPropertyName("settings")] ZoneSettings? Settings);

public sealed record MapMetadata(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("isCurrentMap")] bool IsCurrentMap,
    [property: JsonPropertyName("zones")] List<ZoneMetadata>? Zones,
    [property: JsonPropertyName("imageUrl")] string? ImageUrl);

/// <summary>Grid geometry of a map: width and height in cells, resolution in metres per cell, origin offset in metres.</summary>
public sealed record MapDimensions(
    [property: JsonPropertyName("width")] int Width,
    [property: JsonPropertyName("height")] int Height,
    [property: JsonPropertyName("resolution")] double Resolution,
    [property: JsonPropertyName("offsetX")] double OffsetX,
    [property: JsonPropertyName("offsetY")] double OffsetY);

/// <summary>A planned trajectory segment. Type 0 perimeter, 1 sweep, 2 turn (as observed).</summary>
public sealed record PresentationSegment(
    [property: JsonPropertyName("start")] Point Start,
    [property: JsonPropertyName("end")] Point End,
    [property: JsonPropertyName("type")] int Type);

public sealed record MapZone(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("nameLocation")] Point? NameLocation,
    [property: JsonPropertyName("visited")] List<Point>? Visited,
    [property: JsonPropertyName("presentation")] List<PresentationSegment>? Presentation,
    [property: JsonPropertyName("cleanStatus")] string? CleanStatus,
    [property: JsonPropertyName("area")] double? Area,
    // Present on clean-maps (history list) and clean-maps-data (history detail) zones, but NOT a
    // historical snapshot despite appearing on a specific past clean: confirmed by capture to mirror
    // the map's *current* room preference regardless of which clean is being asked about (a room
    // excluded at launch still read isSelected true once its selection later changed). Use
    // CleanStatus instead to know which rooms an actual past clean touched.
    [property: JsonPropertyName("isSelected")] bool? IsSelected = null,
    [property: JsonPropertyName("settings")] ZoneSettings? Settings = null);

/// <summary>
/// French label for a zone's cleanStatus, as seen in clean-maps-data/live-maps. Confirmed values:
/// CLEAN_NOT_REQUESTED (not selected for this task), CLEAN_COMPLETE, CANT_CLEAN (the robot gave up
/// reaching it, see event.Unable_all_area_recharge.post in docs/protocole.md), CLEAN_PENDING (the
/// room was selected but the task ended before its turn came, seen on a 0-minute clean whose path
/// had a single point); others unconfirmed.
/// </summary>
public static class CleanStatusLabels
{
    public static string Resolve(string? status) => status switch
    {
        "CLEAN_COMPLETE" => "Terminée",
        "CANT_CLEAN" => "Injoignable",
        "CLEAN_NOT_REQUESTED" => "Non demandée",
        "CLEAN_PENDING" => "Non commencée",
        null => "",
        var s => s,
    };
}

public sealed record FurnitureItem(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("userDefined")] bool? UserDefined,
    [property: JsonPropertyName("points")] List<Point>? Points);

/// <summary>
/// What a map-editing jdm call answers. There are two shapes, and which one a method uses is not
/// guessable — it had to be read off the captures:
///   rename_map, set_cur_map            {"result": 0}   0 success, anything else refused
///   rename_room, split_room,           {"map_id": …, "map_type": …, "timestamp": …}
///   arrange_room, set_virtual_wall
/// A null result means refused or unrecognised; the robot has no error field of its own.
/// MapId is null for the first shape, which does not name the map it changed.
/// </summary>
public sealed record MapEditResult(long? MapId, int MapType, long Timestamp)
{
    public static MapEditResult? From(JsonObject? reply)
    {
        if (reply?["data"] is not JsonObject dataNode) return null;
        // The robot repeats keys inside "data" — {"result":0,"result":0} from del_map and
        // set_cur_map, map_id twice from set_virtual_wall — and indexing a JsonObject with a
        // duplicate throws. Re-reading it as a JsonElement walks the members instead, and the
        // first occurrence wins.
        using var doc = JsonDocument.Parse(dataNode.ToJsonString());
        var data = doc.RootElement;

        // Treating a missing "result" as success would turn a refusal into a silent no-op, so the
        // two shapes are told apart by which member is there rather than by the method name.
        if (First(data, "result") is { } r)
            return r.ValueKind == JsonValueKind.Number && r.TryGetInt32(out var code) && code == 0 ? new MapEditResult(null, 0, 0) : null;

        if (First(data, "map_id") is { ValueKind: JsonValueKind.Number } idValue && idValue.TryGetInt64(out var id))
        {
            var type = First(data, "map_type") is { ValueKind: JsonValueKind.Number } t && t.TryGetInt32(out var ti) ? ti : 0;
            var stamp = First(data, "timestamp") is { ValueKind: JsonValueKind.Number } s && s.TryGetInt64(out var sl) ? sl : 0;
            return new MapEditResult(id, type, stamp);
        }
        return null;
    }

    private static JsonElement? First(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object) return null;
        foreach (var p in obj.EnumerateObject())
            if (p.NameEquals(name)) return p.Value;
        return null;
    }
}

public sealed record Restriction(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("behavior")] string? Behavior,
    [property: JsonPropertyName("points")] List<Point>? Points);

/// <summary>GET /v2/app/{serial}/persistent-maps/{mapId}: the stored map with zone geometry.</summary>
public sealed record PersistentMap(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("orientation")] int? Orientation,
    [property: JsonPropertyName("dimensions")] MapDimensions? Dimensions,
    [property: JsonPropertyName("zones")] List<MapZone>? Zones,
    [property: JsonPropertyName("dockLocation")] DockLocation? DockLocation,
    [property: JsonPropertyName("furniture")] List<FurnitureItem>? Furniture,
    [property: JsonPropertyName("restrictions")] List<Restriction>? Restrictions)
{
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; init; }
}

/// <summary>
/// GET /v1/app/{serial}/live-maps/cleaning: the persistent map plus the robot's position and the
/// path of the current or last task. Poll it every few seconds while the robot cleans.
/// </summary>
public sealed record LiveMap(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("taskBeginTime")] long? TaskBeginTime,
    [property: JsonPropertyName("robotLocation")] RobotPosition? RobotLocation,
    [property: JsonPropertyName("zones")] List<MapZone>? Zones,
    [property: JsonPropertyName("cleanPath")] List<Point>? CleanPath,
    [property: JsonPropertyName("dockLocation")] DockLocation? DockLocation,
    [property: JsonPropertyName("furniture")] List<FurnitureItem>? Furniture,
    [property: JsonPropertyName("restrictions")] List<Restriction>? Restrictions,
    [property: JsonPropertyName("obstacles")] List<Point>? Obstacles = null,
    [property: JsonPropertyName("dirt")] List<DirtSpot>? Dirt = null,
    // hazardZones/groutLines/swingDoors: always empty in every response observed so far, shape unconfirmed.
    [property: JsonPropertyName("hazardZones")] List<JsonElement>? HazardZones = null,
    [property: JsonPropertyName("groutLines")] List<JsonElement>? GroutLines = null,
    [property: JsonPropertyName("swingDoors")] List<JsonElement>? SwingDoors = null)
{
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; init; }
}

/// <summary>
/// GET /v1/app/{serial}/live-maps/mapping: the occupancy grid. mapData has width*height cells,
/// row-major, values as produced by the robot's SLAM (0 unknown, others occupied or free).
/// About 300 KB for a 16 m by 21 m home.
/// </summary>
public sealed record MappingMap(
    [property: JsonPropertyName("dimensions")] MapDimensions? Dimensions,
    [property: JsonPropertyName("robotLocation")] RobotPosition? RobotLocation,
    [property: JsonPropertyName("mapData")] List<int>? MapData)
{
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; init; }
}

// ---- Clean history ---------------------------------------------------------------------------

/// <summary>One past clean, from GET /v2/{serial}/clean-maps. Times are Unix seconds.</summary>
public sealed record CleanSummary(
    [property: JsonPropertyName("cleanId")] string CleanId,
    [property: JsonPropertyName("persistentMapId")] string? PersistentMapId,
    [property: JsonPropertyName("isSpotClean")] bool? IsSpotClean,
    [property: JsonPropertyName("startTime")] long? StartTime,
    [property: JsonPropertyName("endTime")] long? EndTime,
    [property: JsonPropertyName("cleanDuration")] int? CleanDurationMinutes,
    [property: JsonPropertyName("areaCleaned")] double? AreaCleanedSquareMetres,
    [property: JsonPropertyName("startBattery")] double? StartBattery,   // sent as 91.0
    [property: JsonPropertyName("endBattery")] double? EndBattery,
    [property: JsonPropertyName("startMethod")] int? StartMethod,
    [property: JsonPropertyName("firmwareVersion")] string? FirmwareVersion,
    [property: JsonPropertyName("zones")] List<MapZone>? Zones,
    [property: JsonPropertyName("faults")] List<JsonElement>? Faults,
    [property: JsonPropertyName("downloadUrl")] string? DownloadUrl)
{
    public DateTimeOffset? Start => StartTime is { } s ? DateTimeOffset.FromUnixTimeSeconds(s) : null;
    public DateTimeOffset? End => EndTime is { } e ? DateTimeOffset.FromUnixTimeSeconds(e) : null;

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; init; }
}

public sealed record CleanList([property: JsonPropertyName("data")] List<CleanSummary> Data);

/// <summary>A stain the robot detected during a clean. "liquid" confirmed; the app is said to show several stain icons, so other types likely exist but haven't been observed yet.</summary>
public sealed record DirtSpot(
    [property: JsonPropertyName("x")] double X,
    [property: JsonPropertyName("y")] double Y,
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("isUvScanOn")] bool? IsUvScanOn);

public sealed record MapBoundary(
    [property: JsonPropertyName("minX")] double MinX,
    [property: JsonPropertyName("maxX")] double MaxX,
    [property: JsonPropertyName("minY")] double MinY,
    [property: JsonPropertyName("maxY")] double MaxY);

/// <summary>GET /v2/{serial}/clean-maps-data/{cleanId}: the path driven and what was found during one clean.</summary>
public sealed record CleanDetail(
    [property: JsonPropertyName("cleanId")] string CleanId,
    [property: JsonPropertyName("persistentMapId")] string? PersistentMapId,
    [property: JsonPropertyName("dimensions")] MapDimensions? Dimensions,
    [property: JsonPropertyName("boundary")] MapBoundary? Boundary,
    [property: JsonPropertyName("zones")] List<MapZone>? Zones,
    [property: JsonPropertyName("cleanPath")] List<Point>? CleanPath,
    [property: JsonPropertyName("dirt")] List<DirtSpot>? Dirt,
    [property: JsonPropertyName("obstacles")] List<Point>? Obstacles,
    [property: JsonPropertyName("dockLocation")] DockLocation? DockLocation,
    [property: JsonPropertyName("furniture")] List<FurnitureItem>? Furniture,
    [property: JsonPropertyName("faults")] List<JsonElement>? Faults,
    // Present in every response but always empty so far ("cable in the way" style hazards, grout
    // lines, swing doors): shape unconfirmed, kept raw until a capture shows one populated.
    [property: JsonPropertyName("hazardZones")] List<JsonElement>? HazardZones = null,
    [property: JsonPropertyName("groutLines")] List<JsonElement>? GroutLines = null,
    [property: JsonPropertyName("swingDoors")] List<JsonElement>? SwingDoors = null)
{
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; init; }
}
