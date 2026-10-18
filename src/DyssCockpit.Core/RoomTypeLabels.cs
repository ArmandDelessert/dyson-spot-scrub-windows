using static DyssCockpit.Core.Translation;

namespace DyssCockpit.Core;

/// <summary>
/// Labels of the thirty room types, in French as the official app shows them, and in English (the other
/// languages are in Translations, as for every text). The app displays a
/// typed room under its type's label and hides the stored name; this app shows the stored name
/// instead (see <see cref="Resolve"/>) and uses the labels for the type picker and as a hint.
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
    private static readonly Dictionary<string, Func<string>> Known = new(StringComparer.Ordinal)
    {
        ["balcony"] = () => T("RoomType_balcony"),
        ["bathroom"] = () => T("RoomType_bathroom"),
        ["bedroom"] = () => T("RoomType_bedroom"),
        ["boxroom"] = () => T("RoomType_boxroom"),
        ["cloakroom"] = () => T("RoomType_cloakroom"),
        ["closet"] = () => T("RoomType_closet"),
        ["conservatory"] = () => T("RoomType_conservatory"),
        ["dining"] = () => T("RoomType_dining"),
        ["ensuite"] = () => T("RoomType_ensuite"),
        ["entrance"] = () => T("RoomType_entrance"),
        ["familyRoom"] = () => T("RoomType_familyRoom"),
        ["guestBathroom"] = () => T("RoomType_guestBathroom"),
        ["guestBedroom"] = () => T("RoomType_guestBedroom"),
        ["guestRoom"] = () => T("RoomType_guestRoom"),
        ["hallway"] = () => T("RoomType_hallway"),
        ["kidsBedroom"] = () => T("RoomType_kidsBedroom"),
        ["kitchen"] = () => T("RoomType_kitchen"),
        ["laundryRoom"] = () => T("RoomType_laundryRoom"),
        ["livingRoom"] = () => T("RoomType_livingRoom"),
        ["nursery"] = () => T("RoomType_nursery"),
        ["office"] = () => T("RoomType_office"),
        ["pantry"] = () => T("RoomType_pantry"),
        ["playRoom"] = () => T("RoomType_playRoom"),
        ["primaryBathroom"] = () => T("RoomType_primaryBathroom"),
        ["primaryBedroom"] = () => T("RoomType_primaryBedroom"),
        ["recreationRoom"] = () => T("RoomType_recreationRoom"),
        ["storageRoom"] = () => T("RoomType_storageRoom"),
        ["study"] = () => T("RoomType_study"),
        ["toilet"] = () => T("RoomType_toilet"),
        ["utilityRoom"] = () => T("RoomType_utilityRoom"),
    };

    /// <summary>
    /// What to show for a room: always its stored name. Unlike the phone app, which shows a typed
    /// room under its type's label and hides the name, what the user named a room is what they
    /// see, everywhere. The type's label only stands in when there is no name at all — never seen
    /// from the robot, but cheaper to handle than to crash on — and the zone id after that.
    /// </summary>
    public static string Resolve(string? type, string? storedName, string fallback)
    {
        if (!string.IsNullOrEmpty(storedName)) return storedName;
        return DefaultNameFor(type) ?? fallback;
    }

    /// <summary>
    /// The type to show beside a room's name where both are listed, or empty when it would only
    /// repeat the name. A room with no type or type "custom" reads "Personnalisée"; a type this
    /// table does not know (a future firmware's) is shown raw rather than hidden.
    /// </summary>
    public static string TypeHint(string? type, string? storedName)
    {
        if (string.IsNullOrEmpty(type) || type == "custom") return T("RoomTypeLabels_Custom");
        if (DefaultNameFor(type) is not { } label) return type;
        return label == Resolve(type, storedName, "") ? "" : label;
    }

    /// <summary>The default name the robot gives a room of this type, used to prefill a rename.</summary>
    public static string? DefaultNameFor(string? type) =>
        type is not null && Known.TryGetValue(type, out var label) ? label() : null;

    /// <summary>Every known type with its label, for a type picker. "custom" is not in here: it has no label of its own.</summary>
    public static IReadOnlyList<(string Type, string Label)> All =>
        [.. Known.Select(kv => (kv.Key, kv.Value())).OrderBy(t => t.Item2, StringComparer.CurrentCulture)];
}
