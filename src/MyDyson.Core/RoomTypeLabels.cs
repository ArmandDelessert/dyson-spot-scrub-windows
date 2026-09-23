namespace MyDyson.Core;

/// <summary>
/// French labels the official app shows for a typed room, in place of whatever is stored in the
/// zone's own "name" field.
///
/// Established by decompiling the room-type enum (class d11.a: thirty types, BALCONY through
/// UTILITY_ROOM plus CUSTOM, declared alphabetically with a localisation key per entry) and
/// confirmed for all thirty against real account data: a test map was split into one zone per
/// type, and the label the app displayed for each was cross-checked against that zone's REST
/// "type" field. Several stored names even carry a numeric suffix the app never shows
/// ("Chambre1", "Salon12"..."Salon15", "Salle de bain1"): Dyson auto-fills a new zone's stored
/// name with the type's own default label, and appends a number only to keep the raw storage
/// unique when several zones share a type, since the display never uses that field for a typed
/// room anyway. A "custom"-type room (no default exists) always shows its stored name unchanged,
/// e.g. "Pièce1". The stored name therefore is not what the room list displays for a typed room;
/// it likely still matters elsewhere, such as voice assistant references.
/// </summary>
public static class RoomTypeLabels
{
    private static readonly Dictionary<string, string> Known = new(StringComparer.Ordinal)
    {
        ["balcony"] = "Balcon",
        ["bathroom"] = "Salle de bain",
        ["bedroom"] = "Chambre",
        ["boxroom"] = "Cagibi",
        ["cloakroom"] = "Toilettes",
        ["closet"] = "Dressing",
        ["conservatory"] = "Véranda",
        ["dining"] = "Salle à manger",
        ["ensuite"] = "Salle de bain attenante",
        ["entrance"] = "Hall d'entrée",
        ["familyRoom"] = "Pièce familiale",
        ["guestBathroom"] = "Salle de bain invités",
        ["guestBedroom"] = "Chambre d'amis",
        ["guestRoom"] = "Chambre invités",
        ["hallway"] = "Couloir",
        ["kidsBedroom"] = "Chambre d'enfant",
        ["kitchen"] = "Cuisine",
        ["laundryRoom"] = "Buanderie",
        ["livingRoom"] = "Salon",
        ["nursery"] = "Chambre de bébé",
        ["office"] = "Bureau",
        ["pantry"] = "Cellier",
        ["playRoom"] = "Salle de jeux",
        ["primaryBathroom"] = "Salle de bain parentale",
        ["primaryBedroom"] = "Chambre parentale",
        ["recreationRoom"] = "Salle de loisirs",
        ["storageRoom"] = "Débarras",
        ["study"] = "Bibliothèque",
        ["toilet"] = "W.-C.",
        ["utilityRoom"] = "Cave",
    };

    /// <summary>
    /// What to show for a room. A typed room whose stored name is just the type's own default
    /// ("Chambre", or "Chambre1" — Dyson appends a digit to keep storage unique) shows the type's
    /// label, exactly like the phone app. A stored name the user actually chose ("Chambre d'amis")
    /// wins instead: the app hides it, but hiding it here would make a rename from this window look
    /// as though nothing had happened.
    /// </summary>
    public static string Resolve(string? type, string? storedName, string fallback)
    {
        if (string.IsNullOrEmpty(type) || !Known.TryGetValue(type, out var label)) return storedName ?? fallback;
        return string.IsNullOrEmpty(storedName) || IsDefaultFor(storedName, label) ? label : storedName;
    }

    /// <summary>The label itself, or the label followed only by digits.</summary>
    private static bool IsDefaultFor(string storedName, string label) =>
        storedName.StartsWith(label, StringComparison.Ordinal)
        && storedName.AsSpan(label.Length).ToString().All(char.IsAsciiDigit);

    /// <summary>The default name the robot gives a room of this type, used to prefill a rename.</summary>
    public static string? DefaultNameFor(string? type) =>
        type is not null && Known.TryGetValue(type, out var label) ? label : null;

    /// <summary>Every known type with its label, for a type picker. "custom" is not in here: it has no label of its own.</summary>
    public static IReadOnlyList<(string Type, string Label)> All { get; } =
        [.. Known.Select(kv => (kv.Key, kv.Value)).OrderBy(t => t.Value, StringComparer.CurrentCulture)];
}
