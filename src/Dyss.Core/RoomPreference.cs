using System.Text.Json;
using System.Text.Json.Nodes;

namespace Dyss.Core;

/// <summary>
/// One entry of the jdm room_preference array. The robot stores rooms as positional arrays, not
/// objects: index 0 is the zone id, index 1 the name, the rest are per-room settings whose meaning
/// is only partly known. Replies carry twelve elements, the app publishes eleven.
/// The name is either a plain string or a JSON-encoded object {"type": "...", "name": "..."} for
/// rooms the user assigned a type to.
/// </summary>
public sealed record RoomPreference(int Id, string Name, string? Type, IReadOnlyList<JsonNode?> Rest, JsonArray Raw)
{
    public static RoomPreference? Parse(JsonNode? node)
    {
        if (node is not JsonArray arr || arr.Count < 2) return null;
        if (arr[0] is not JsonValue idValue || !idValue.TryGetValue<int>(out var id)) return null;

        var rawName = arr[1] is JsonValue nv && nv.TryGetValue<string>(out var s) ? s : "";
        var (name, type) = SplitName(rawName);
        var rest = arr.Skip(2).ToList();
        return new RoomPreference(id, name, type, rest, arr);
    }

    /// <summary>Decodes the dual-form name field. Returns (name, type); type is null for plain names.</summary>
    public static (string Name, string? Type) SplitName(string raw)
    {
        if (raw.StartsWith('{'))
        {
            try
            {
                using var doc = JsonDocument.Parse(raw);
                var root = doc.RootElement;
                var name = root.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString()! : raw;
                var type = root.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
                return (name, type);
            }
            catch (JsonException) { /* not JSON after all */ }
        }
        return (raw, null);
    }

    /// <summary>Encodes a name the way the app does when a type is set.</summary>
    public static string JoinName(string name, string? type) =>
        type is null ? name : new JsonObject { ["type"] = type, ["name"] = name }.ToJsonString();

    public static List<RoomPreference> ParseAll(JsonNode? roomArray) =>
        roomArray is JsonArray arr ? arr.Select(Parse).Where(r => r is not null).Select(r => r!).ToList() : [];
}
