using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DyssCockpit.Presentation.Map;
using DyssCockpit.Core;

using static DyssCockpit.Core.Translation;

namespace DyssCockpit.Presentation.ViewModels;

/// <summary>A room type offered in the rename dialog. Type null is the free-name escape hatch.</summary>
public sealed record RoomTypeOption(string? Type, string Label)
{
    public override string ToString() => Label;
}

/// <summary>One room of the map being managed.</summary>
public sealed partial class ManagedRoom(MapZone Zone) : ObservableObject
{
    public MapZone Zone { get; } = Zone;
    public string Id => Zone.Id;
    public int NumericId => int.TryParse(Zone.Id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : -1;
    public string DisplayName => RoomTypeLabels.Resolve(Zone.Type, Zone.Name, Zone.Id);
    public string AreaText => Zone.Area is { } a ? $"{a:F1} m²" : "";
    /// <summary>The type beside the name, only when it says something the name does not (see <see cref="RoomTypeLabels.TypeHint"/>).</summary>
    public string TypeText => RoomTypeLabels.TypeHint(Zone.Type, Zone.Name);

    /// <summary>Highlighted in the list and on the map: the chosen room, or one of those gathered for a merge.</summary>
    [ObservableProperty] private bool _isChosen;
}

/// <summary>Which kind of map object clicks and actions are about: one tab of the right-hand panel each.</summary>
public enum MapLayer { Rooms, Zones, Furniture }

/// <summary>One restriction zone of the map being managed.</summary>
public sealed partial class ManagedZone(Restriction restriction, int sourceIndex) : ObservableObject
{
    public Restriction Restriction { get; } = restriction;
    /// <summary>Its place in the cloud's list: the list on screen is sorted, what goes back to the robot keeps this order.</summary>
    public int SourceIndex { get; } = sourceIndex;
    public string Id => Restriction.Id ?? "";
    /// <summary>Null for a behaviour this app does not know, which it could not send back.</summary>
    public RestrictionKind? Kind { get; } = RestrictionKind.FromBehavior(restriction.Behavior);
    public IReadOnlyList<DyssCockpit.Core.Point> Corners => Restriction.Points ?? [];
    public bool CanBeSentBack => Kind is not null && Corners.Count == 4;
    public string Label => Kind?.Label ?? T($"Type inconnu ({Restriction.Behavior})", $"Unknown type ({Restriction.Behavior})");
    public string SizeText => MapManagerViewModel.SizeOf(Corners);
    /// <summary>The zone's colour on the map, for the list.</summary>
    public ArgbColor Swatch { get; } = MapColors.Restriction(restriction.Behavior);
    [ObservableProperty] private bool _isChosen;
}

/// <summary>One piece of furniture of the map being managed.</summary>
public sealed partial class ManagedFurniture(FurnitureItem item) : ObservableObject
{
    public FurnitureItem Item { get; } = item;
    public string Id => Item.Id;
    /// <summary>Null for a type this app has no code for, which it could not send back.</summary>
    public FurnitureKind? Kind { get; } = FurnitureKind.FromRestType(item.Type);
    public IReadOnlyList<DyssCockpit.Core.Point> Corners => Item.Points ?? [];
    public int Index => int.TryParse(Item.Id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : -1;
    public bool CanBeSentBack => Kind is not null && Corners.Count == 4 && Index >= 0;
    public string Label => Kind?.Label ?? T($"Meuble inconnu ({Item.Type})", $"Unknown furniture ({Item.Type})");
    public string SizeText => MapManagerViewModel.SizeOf(Corners);
    /// <summary>Clockwise quarter turns from the way a new piece is laid out, or null at another angle.</summary>
    public int? QuarterTurns => MapShapes.QuarterTurns(Corners);
    [ObservableProperty] private bool _isChosen;
}

/// <summary>One of the four ways a piece of furniture can face.</summary>
public sealed record OrientationOption(int QuarterTurns, string Label)
{
    public static readonly IReadOnlyList<OrientationOption> All =
    [
        new(0, T("0° (comme posé)", "0° (as placed)")),
        new(1, T("90° (quart de tour horaire)", "90° (quarter turn clockwise)")),
        new(2, T("180° (demi-tour)", "180° (half turn)")),
        new(3, T("270° (quart de tour antihoraire)", "270° (quarter turn anticlockwise)")),
    ];

    public override string ToString() => Label;
}
