using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DyssCockpit.Presentation.Map;
using DyssCockpit.Core;

using static DyssCockpit.Core.Translation;

namespace DyssCockpit.Presentation.ViewModels;

/// <summary>The zones tab: the restriction zones of the map, drawn, retyped, moved, resized or deleted, all sent back as one list.</summary>
public sealed partial class MapManagerViewModel
{
    // ---- The zones on the map -----------------------------------------------------

    public ObservableCollection<ManagedZone> RestrictionZones { get; } = new();
    [ObservableProperty] private ManagedZone? _selectedZone;
    public IReadOnlyList<RestrictionKind> ZoneKinds { get; } = RestrictionKind.All;
    /// <summary>The kind a new zone gets, and the one "Changer le type" turns the chosen zone into.</summary>
    [ObservableProperty] private RestrictionKind _zoneKind = RestrictionKind.All[0];

    /// <summary>Why the zones cannot be changed on this map, or empty when they can.</summary>
    public string ZonesBlockedReason => RestrictionZones.FirstOrDefault(z => !z.CanBeSentBack) is { } odd
        ? T($"Cette carte porte une zone que cette application ne sait pas décrire ({odd.Label}) : toute modification l'effacerait, les zones restent donc en lecture seule.", $"This map holds a zone this application cannot describe ({odd.Label}): any change would erase it, so the zones are read-only.")
        : "";

    public string ZonesEmptyText => RestrictionZones.Count == 0 ? T("Aucune zone sur cette carte.", "No zone on this map.") : "";
    private bool ZonesEditable => IsActiveMap && Idle && ZonesBlockedReason == "";
    private bool CanAddZone() => ZonesEditable;
    private bool CanDeleteZone() => SelectedZone is not null && ZonesEditable;
    /// <summary>Only offered when the kind picked differs from the chosen zone's: otherwise there is nothing to change.</summary>
    private bool CanChangeZoneKind() => SelectedZone is { Kind: { } k } && k != ZoneKind && ZonesEditable;

    private ManagedZone? ZoneAt(DyssCockpit.Core.Point p) =>
        RestrictionZones.Where(z => z.Corners.Count > 2 && MapShapes.Contains(z.Corners, p.X, p.Y))
            .OrderBy(z => MapShapes.Area(z.Corners)).FirstOrDefault();

    [RelayCommand]
    private void ClickZone(ManagedZone? zone)
    {
        if (zone is not null && !Busy && !InMode) SelectedZone = ReferenceEquals(SelectedZone, zone) ? null : zone;
    }

    partial void OnSelectedZoneChanged(ManagedZone? oldValue, ManagedZone? newValue)
    {
        if (oldValue is not null) oldValue.IsChosen = false;
        if (newValue is not null)
        {
            newValue.IsChosen = true;
            // The kind picker starts from the chosen zone's own, so "Changer le type" only lights
            // up once another kind is picked.
            if (newValue.Kind is { } k) ZoneKind = k;
        }
        RebuildScene();
        RefreshCommandStates();
    }

    partial void OnZoneKindChanged(RestrictionKind value) => ChangeZoneKindCommand.NotifyCanExecuteChanged();

    // ---- Zone commands -----------------------------------------------------------

    /// <summary>The smallest zone side accepted; a narrower one would be a slip of the mouse rather than a zone.</summary>
    public const double MinimumZoneSide = 0.2;

    [RelayCommand(CanExecute = nameof(CanAddZone))]
    private void AddZone()
    {
        AddingZone = true;
        Hint = SnapToGrid
            ? T($"Cliquez deux coins opposés de la zone « {ZoneKind.Label} » ; ils se calent sur la grille de 5 cm, visible en zoomant. Échap pour annuler.",
                $"Click two opposite corners of the “{ZoneKind.Label}” zone; they snap to the 5 cm grid, visible when zoomed in. Esc to cancel.")
            : T($"Cliquez deux coins opposés de la zone « {ZoneKind.Label} » ; sans calage sur la grille. Échap pour annuler.",
                $"Click two opposite corners of the “{ZoneKind.Label}” zone; no snapping to the grid. Esc to cancel.");
        Status = "";
        RefreshCommandStates();
    }

    /// <summary>Called by the map once both corners of the new zone have been clicked.</summary>
    public async Task ZoneDrawnAsync(DyssCockpit.Core.Point a, DyssCockpit.Core.Point b)
    {
        var kind = ZoneKind;
        LeaveModes();
        if (Math.Abs(a.X - b.X) < MinimumZoneSide - MapShapes.Tolerance || Math.Abs(a.Y - b.Y) < MinimumZoneSide - MapShapes.Tolerance)
        {
            Status = T("Zone trop étroite : il faut au moins 20 cm de côté.", "Zone too narrow: each side needs at least 20 cm.");
            return;
        }
        var zones = CurrentZones();
        zones.Add(new RestrictionZone(kind, MapShapes.Rectangle(a, b)));
        await SendZonesAsync(T($"zone « {kind.Label} » ajoutée", $"“{kind.Label}” zone added"), zones);
    }

    [RelayCommand(CanExecute = nameof(CanChangeZoneKind))]
    private async Task ChangeZoneKindAsync()
    {
        if (SelectedZone is not { } chosen) return;
        var kind = ZoneKind;
        var zones = OrderedZones().Select(z => new RestrictionZone(ReferenceEquals(z, chosen) ? kind : z.Kind!, z.Corners)).ToList();
        await SendZonesAsync(T($"zone changée en « {kind.Label} »", $"zone changed to “{kind.Label}”"), zones);
    }

    [RelayCommand(CanExecute = nameof(CanDeleteZone))]
    private async Task DeleteZoneAsync()
    {
        if (SelectedZone is not { } chosen) return;
        if (!await _hub.Dialogs.ConfirmAsync(T("Supprimer la zone", "Delete the zone"), T($"Supprimer la zone « {chosen.Label} » ({chosen.SizeText}) ?", $"Delete the “{chosen.Label}” zone ({chosen.SizeText})?"))) return;
        var zones = OrderedZones().Where(z => !ReferenceEquals(z, chosen)).Select(z => new RestrictionZone(z.Kind!, z.Corners)).ToList();
        await SendZonesAsync(T($"zone « {chosen.Label} » supprimée", $"“{chosen.Label}” zone deleted"), zones);
    }

    /// <summary>The zones in the cloud's order rather than the list's alphabetical one, so a change moves nothing else around.</summary>
    private IEnumerable<ManagedZone> OrderedZones() => RestrictionZones.OrderBy(z => z.SourceIndex);

    private List<RestrictionZone> CurrentZones() => [.. OrderedZones().Select(z => new RestrictionZone(z.Kind!, z.Corners))];

    private async Task SendZonesAsync(string label, IReadOnlyList<RestrictionZone> zones)
    {
        if (SelectedMap is not { } map || !TryMapId(map, out var mapId)) return;
        await EditAsync(label, c => c.SetRestrictionsAsync(mapId, zones, _hub.Ct), refused: T("Le robot a refusé les zones.", "The robot refused the zones."));
    }
}
