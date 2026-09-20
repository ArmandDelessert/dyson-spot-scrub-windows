namespace MyDyson.Core;

/// <summary>
/// French labels the official app shows for a typed room, in place of whatever is stored in the
/// zone's own "name" field.
///
/// Established by decompiling the room-type enum (class d11.a: thirty types, BALCONY through
/// UTILITY_ROOM plus CUSTOM, declared alphabetically with a localisation key per entry) and by
/// cross-checking confirmed pairs against real account data: a room of type "toilet" whose stored
/// name is "Salle de bain" is shown by the app as "W.-C.", and a "livingRoom" room stored as
/// "Salon2" is shown as "Salon". A "custom"-type room (no default exists) always shows its stored
/// name unchanged, e.g. "Pièce1". The stored name therefore is not what the room list displays for
/// a typed room; it likely still matters elsewhere, such as voice assistant references.
///
/// Only pairs actually observed are listed here; every other type falls back to the stored name,
/// which is what this library already showed before this file existed.
/// </summary>
public static class RoomTypeLabels
{
    private static readonly Dictionary<string, string> Known = new(StringComparer.Ordinal)
    {
        ["kitchen"] = "Cuisine",
        ["hallway"] = "Couloir",
        ["bedroom"] = "Chambre",
        ["livingRoom"] = "Salon",
        ["bathroom"] = "Salle de bain",
        ["toilet"] = "W.-C.",
        ["office"] = "Bureau",
        ["dining"] = "Salle à manger",
    };

    /// <summary>The label the app would show: the type's own label when known, else the stored name.</summary>
    public static string Resolve(string? type, string? storedName, string fallback) =>
        !string.IsNullOrEmpty(type) && Known.TryGetValue(type, out var label) ? label : storedName ?? fallback;
}
