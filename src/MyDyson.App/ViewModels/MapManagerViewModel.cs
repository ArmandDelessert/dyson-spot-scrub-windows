using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MyDyson.App.Rendering;
using MyDyson.App.Services;
using MyDyson.Core;

namespace MyDyson.App.ViewModels;

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
public sealed partial class ManagedZone(Restriction restriction) : ObservableObject
{
    public Restriction Restriction { get; } = restriction;
    public string Id => Restriction.Id ?? "";
    /// <summary>Null for a behaviour this app does not know, which it could not send back.</summary>
    public RestrictionKind? Kind { get; } = RestrictionKind.FromBehavior(restriction.Behavior);
    public IReadOnlyList<MyDyson.Core.Point> Corners => Restriction.Points ?? [];
    public bool CanBeSentBack => Kind is not null && Corners.Count == 4;
    public string Label => Kind?.Label ?? $"Type inconnu ({Restriction.Behavior})";
    public string SizeText => MapManagerViewModel.SizeOf(Corners);
    /// <summary>The zone's colour on the map, for the list.</summary>
    public System.Windows.Media.Brush Swatch { get; } = Frozen(MapRenderer.RestrictionColor(restriction.Behavior));
    [ObservableProperty] private bool _isChosen;

    private static System.Windows.Media.SolidColorBrush Frozen(System.Windows.Media.Color c)
    {
        var b = new System.Windows.Media.SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}

/// <summary>One piece of furniture of the map being managed.</summary>
public sealed partial class ManagedFurniture(FurnitureItem item) : ObservableObject
{
    public FurnitureItem Item { get; } = item;
    public string Id => Item.Id;
    /// <summary>Null for a type this app has no code for, which it could not send back.</summary>
    public FurnitureKind? Kind { get; } = FurnitureKind.FromRestType(item.Type);
    public IReadOnlyList<MyDyson.Core.Point> Corners => Item.Points ?? [];
    public int Index => int.TryParse(Item.Id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : -1;
    public bool CanBeSentBack => Kind is not null && Corners.Count == 4 && Index >= 0;
    public string Label => Kind?.Label ?? $"Meuble inconnu ({Item.Type})";
    public string SizeText => MapManagerViewModel.SizeOf(Corners);
    [ObservableProperty] private bool _isChosen;
}

/// <summary>
/// The Gérer les cartes window: rename, delete or activate a map, rename rooms, merge them, cut one
/// in two, and run a new mapping scan. It works off its own copy of the map rather than the
/// dashboard's, since every edit makes the robot re-save the map and the result has to be re-read.
///
/// The room list has two modes. Normally a click chooses one room, which renaming and splitting
/// act on. Once "Fusionner" is pressed, clicks gather rooms instead, and pressing it again merges
/// them — no check boxes, so a room is never both chosen and ticked. A click on empty map space
/// clears whatever is highlighted, in either mode.
///
/// What the robot does not offer, and so is absent here: deleting a room, and setting a room's
/// outline. Splitting only ever cuts along a straight line, and the robot snaps that line to its
/// own occupancy grid, so a room's shape can be steered but not dictated.
///
/// Restriction zones and furniture have a tab each (<see cref="Layer"/>): there a click on the map
/// chooses a zone or a piece instead of a room. Both are sent to the robot as whole lists — every
/// change resends everything else as the cloud last gave it back — so a zone or a piece this app
/// cannot describe would be lost by any change, and its whole layer is then left read-only.
/// </summary>
public sealed partial class MapManagerViewModel : ObservableObject
{
    private readonly RobotHub _hub;
    private readonly DisplaySettings _display;
    /// <summary>The rooms gathered for a merge, in click order: the robot receives them in that order.</summary>
    private readonly List<ManagedRoom> _mergeSet = [];

    public MapManagerViewModel(RobotHub hub, DisplaySettings display)
    {
        _hub = hub;
        _display = display;
        RoomTypes = [new RoomTypeOption(null, "Personnalisée (nom libre)"),
                     .. RoomTypeLabels.All.Select(t => new RoomTypeOption(t.Type, t.Label))];
    }

    public ObservableCollection<MapItem> Maps { get; } = new();
    [ObservableProperty] private MapItem? _selectedMap;
    public ObservableCollection<ManagedRoom> Rooms { get; } = new();
    [ObservableProperty] private ManagedRoom? _selectedRoom;
    [ObservableProperty] private MapScene _scene = new();
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _busy;

    /// <summary>The 30 known types plus the free-name option, for the rename dialog.</summary>
    public IReadOnlyList<RoomTypeOption> RoomTypes { get; }

    /// <summary>True while the user is aiming a cut on the map; the view turns clicks into points.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(InMode))] private bool _splitting;
    /// <summary>True while clicks gather rooms to merge rather than choose one.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(InMode))] private bool _merging;
    /// <summary>True while the user is drawing a restriction zone's rectangle on the map.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(InMode))] private bool _addingZone;
    /// <summary>True while the user is choosing where a new piece of furniture goes.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(InMode)), NotifyPropertyChangedFor(nameof(PlacementShape))] private bool _placingFurniture;
    /// <summary>True while the user is choosing where the chosen piece of furniture moves to.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(InMode)), NotifyPropertyChangedFor(nameof(PlacementShape))] private bool _movingFurniture;
    /// <summary>An edit is being aimed or gathered, which is when there is something to cancel.</summary>
    public bool InMode => Merging || Splitting || AddingZone || PlacingFurniture || MovingFurniture;
    /// <summary>What the current mode expects from the user, shown above the map.</summary>
    [ObservableProperty] private string _hint = RoomsHint;
    [ObservableProperty] private string _mergeButtonLabel = "Fusionner des pièces…";

    private const string RoomsHint = "Cliquez une pièce pour la choisir, en dehors pour effacer le choix. Molette pour zoomer, glisser pour déplacer.";
    private const string ZonesHint = "Cliquez une zone pour la choisir, en dehors pour effacer le choix. Molette pour zoomer, glisser pour déplacer.";
    private const string FurnitureHint = "Cliquez un meuble pour le choisir, en dehors pour effacer le choix. Molette pour zoomer, glisser pour déplacer.";

    private string LayerHint => Layer switch
    {
        MapLayer.Zones => ZonesHint,
        MapLayer.Furniture => FurnitureHint,
        _ => RoomsHint,
    };

    // ---- Zones and furniture ---------------------------------------------------------

    /// <summary>The tab on show; the TabControl binds <see cref="LayerIndex"/>.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(LayerIndex))] private MapLayer _layer;
    public int LayerIndex
    {
        get => (int)Layer;
        set => Layer = (MapLayer)value;
    }

    public ObservableCollection<ManagedZone> RestrictionZones { get; } = new();
    [ObservableProperty] private ManagedZone? _selectedZone;
    public IReadOnlyList<RestrictionKind> ZoneKinds { get; } = RestrictionKind.All;
    /// <summary>The kind a new zone gets, and the one "Changer le type" turns the chosen zone into.</summary>
    [ObservableProperty] private RestrictionKind _zoneKind = RestrictionKind.All[0];

    public ObservableCollection<ManagedFurniture> FurnitureItems { get; } = new();
    [ObservableProperty] private ManagedFurniture? _selectedFurniture;
    public IReadOnlyList<FurnitureKind> FurnitureKinds { get; } = FurnitureKind.All;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(PlacementShape))] private FurnitureKind _furnitureKind = FurnitureKind.All[0];

    /// <summary>
    /// The outline to show under the cursor while placing or moving a piece, as corners relative
    /// to its centre: the new piece at its default size, or the chosen one as it stands.
    /// </summary>
    public IReadOnlyList<MyDyson.Core.Point>? PlacementShape
    {
        get
        {
            if (PlacingFurniture) return MapShapes.Centred(new(0, 0), FurnitureKind.Length, FurnitureKind.Width);
            if (MovingFurniture && SelectedFurniture is { } f)
            {
                var c = MapShapes.Centre(f.Corners);
                return MapShapes.Translate(f.Corners, -c.X, -c.Y);
            }
            return null;
        }
    }

    /// <summary>Why the zones cannot be changed on this map, or empty when they can.</summary>
    public string ZonesBlockedReason => RestrictionZones.FirstOrDefault(z => !z.CanBeSentBack) is { } odd
        ? $"Cette carte porte une zone que cette application ne sait pas décrire ({odd.Label}) : toute modification l'effacerait, les zones restent donc en lecture seule."
        : "";

    /// <summary>Why the furniture cannot be changed on this map, or empty when it can.</summary>
    public string FurnitureBlockedReason => FurnitureItems.FirstOrDefault(f => !f.CanBeSentBack) is { } odd
        ? $"Cette carte porte un meuble que cette application ne sait pas décrire ({odd.Label}) : toute modification l'effacerait, les meubles restent donc en lecture seule."
        : "";

    public string ZonesEmptyText => RestrictionZones.Count == 0 ? "Aucune zone sur cette carte." : "";
    public string FurnitureEmptyText => FurnitureItems.Count == 0 ? "Aucun meuble sur cette carte." : "";

    /// <summary>"1,2 × 0,5 m": the first two sides of a shape.</summary>
    internal static string SizeOf(IReadOnlyList<MyDyson.Core.Point> corners)
    {
        var (a, b) = MapShapes.Sides(corners);
        return string.Create(CultureInfo.CurrentCulture, $"{a:0.0} × {b:0.0} m");
    }

    /// <summary>Asks the window to show a text prompt; returns null when the user cancels.</summary>
    public Func<string, string, string, string?>? AskForText { get; set; }
    /// <summary>Asks the window for a room name and type; returns null when the user cancels.</summary>
    public Func<ManagedRoom, IReadOnlyList<RoomTypeOption>, (string Name, string? Type)?>? AskForRoomName { get; set; }
    /// <summary>Asks the window for a yes/no confirmation.</summary>
    public Func<string, string, bool>? Confirm { get; set; }

    /// <summary>Raised after a successful edit so the dashboard can pick up the new names.</summary>
    public event Action? Changed;

    // ---- Loading ---------------------------------------------------------------

    public async Task LoadAsync(string? preferredMapId = null)
    {
        try
        {
            var wanted = preferredMapId ?? SelectedMap?.Id;
            var metadata = await _hub.Api.GetMapMetadataAsync(_hub.Serial, _hub.Ct);
            Maps.Clear();
            foreach (var m in MapItem.InDisplayOrder(metadata)) Maps.Add(new MapItem(m));
            SelectedMap = Maps.FirstOrDefault(m => m.Id == wanted)
                ?? Maps.FirstOrDefault(m => m.Metadata.IsCurrentMap)
                ?? Maps.FirstOrDefault();
        }
        catch (OperationCanceledException) when (_hub.IsShuttingDown) { }
        catch (Exception ex) { Status = $"Cartes : {ex.Message}"; }
    }

    partial void OnSelectedMapChanged(MapItem? value)
    {
        // Whatever was chosen, gathered or being cut belonged to the map that was on screen.
        LeaveModes();
        SelectedRoom = null;
        SelectedZone = null;
        SelectedFurniture = null;
        OnPropertyChanged(nameof(IsActiveMap));
        OnPropertyChanged(nameof(EditBlockedReason));
        RefreshCommandStates();
        if (value is not null) _ = LoadMapAsync(value);
    }

    private async Task LoadMapAsync(MapItem item)
    {
        try
        {
            var map = await _hub.Api.GetPersistentMapAsync(_hub.Serial, item.Id, _hub.Ct);
            MapGrid? grid = null;
            // Only the active map has an occupancy grid; the others show their visited points.
            if (item.Metadata.IsCurrentMap)
                grid = MapGrid.From(await _hub.Api.GetMappingMapAsync(_hub.Serial, _hub.Ct));

            Rooms.Clear();
            foreach (var z in (map.Zones ?? []).OrderBy(z => RoomTypeLabels.Resolve(z.Type, z.Name, z.Id), StringComparer.CurrentCulture))
                Rooms.Add(new ManagedRoom(z));
            // Kept in the cloud's own order, which is also the order they are sent back in.
            RestrictionZones.Clear();
            foreach (var r in map.Restrictions ?? []) RestrictionZones.Add(new ManagedZone(r));
            FurnitureItems.Clear();
            foreach (var f in map.Furniture ?? []) FurnitureItems.Add(new ManagedFurniture(f));
            OnPropertyChanged(nameof(ZonesBlockedReason));
            OnPropertyChanged(nameof(FurnitureBlockedReason));
            OnPropertyChanged(nameof(ZonesEmptyText));
            OnPropertyChanged(nameof(FurnitureEmptyText));
            _map = map;
            _grid = grid;
            RefreshCommandStates();
            RebuildScene();
        }
        catch (OperationCanceledException) when (_hub.IsShuttingDown) { }
        catch (Exception ex) { Status = $"Carte {item.Id} : {ex.Message}"; }
    }

    private PersistentMap? _map;
    private MapGrid? _grid;

    private void RebuildScene() => Scene = new MapScene
    {
        Grid = _grid,
        Map = _map,
        ZoneMetadata = SelectedMap?.Metadata.Zones,
        Dock = _map?.DockLocation,
        // What an action is about to touch is highlighted, the rest dimmed.
        SelectedZoneIds = Rooms.Any(r => r.IsChosen) ? Rooms.Where(r => r.IsChosen).Select(r => r.Id).ToHashSet(StringComparer.Ordinal) : null,
        SelectedRestrictionId = SelectedZone?.Id,
        SelectedFurnitureId = SelectedFurniture?.Id,
        // The furniture tab shows it whatever the display option says: it is what is being edited.
        ShowFurniture = _display.ShowFurniture || Layer == MapLayer.Furniture,
        ShowTravelPath = _display.ShowTravelPath,
    };

    // ---- Choosing rooms ----------------------------------------------------------

    /// <summary>A click on a room, from the list or the map. Chooses it, or gathers it for a merge.</summary>
    public void RoomClicked(ManagedRoom room)
    {
        if (Splitting || Busy || Layer != MapLayer.Rooms) return;
        if (Merging)
        {
            if (!_mergeSet.Remove(room)) _mergeSet.Add(room);
            room.IsChosen = _mergeSet.Contains(room);
            UpdateMergeLabel();
        }
        else
        {
            // Clicking the chosen room again lets go of it, as on the dashboard's map.
            SelectedRoom = ReferenceEquals(SelectedRoom, room) ? null : room;
        }
        RebuildScene();
    }

    [RelayCommand]
    private void ClickRoom(ManagedRoom? room)
    {
        if (room is not null) RoomClicked(room);
    }

    /// <summary>Called by the window when a room is clicked on the map.</summary>
    public void RoomClickedById(string zoneId)
    {
        if (Rooms.FirstOrDefault(r => r.Id == zoneId) is { } room) RoomClicked(room);
    }

    /// <summary>A click on empty map space: lets go of whatever is highlighted, in either mode.</summary>
    public void ClearRoomSelection()
    {
        if (Splitting || Layer != MapLayer.Rooms) return;
        if (Merging)
        {
            foreach (var r in _mergeSet) r.IsChosen = false;
            _mergeSet.Clear();
            UpdateMergeLabel();
            RebuildScene();
        }
        else
        {
            SelectedRoom = null;
        }
    }

    partial void OnSelectedRoomChanged(ManagedRoom? oldValue, ManagedRoom? newValue)
    {
        if (oldValue is not null) oldValue.IsChosen = false;
        if (newValue is not null) newValue.IsChosen = true;
        RebuildScene();
        RefreshCommandStates();
    }

    // ---- What each action needs before it can be offered ----------------------

    /// <summary>
    /// Every edit is kept to the active map. Across 36 captured edits, from the phone app and from
    /// this one, the target was always the active map — editing another one was never observed —
    /// and on 2026-09-23 a map came out renamed after the active one and stripped of its rooms, in
    /// an uncaptured stretch where it was likely being edited from here while not active. Until
    /// that path is understood, the other maps are read-only here; "Définir comme active" is one
    /// click away.
    /// </summary>
    public bool IsActiveMap => SelectedMap?.Metadata.IsCurrentMap == true;

    /// <summary>Why the edits are unavailable on the map on screen, or empty when they are available.</summary>
    public string EditBlockedReason => SelectedMap is null || IsActiveMap ? ""
        : "Seule la carte active peut être modifiée. Définissez celle-ci comme active pour la renommer, la supprimer ou changer ses pièces, ses zones ou ses meubles.";

    private bool Idle => !Busy && !InMode;
    private bool NotBusy() => Idle;
    private bool HasMap() => SelectedMap is not null && IsActiveMap && Idle;
    private bool HasRoom() => SelectedRoom is not null && IsActiveMap && Idle;
    private bool CanSetActive() => SelectedMap is { Metadata.IsCurrentMap: false } && Idle;
    /// <summary>Entering merge mode needs two rooms to exist; confirming it needs two gathered.</summary>
    private bool CanMerge() => !Busy && (Merging || !InMode) && IsActiveMap && (Merging ? _mergeSet.Count >= 2 : Rooms.Count >= 2);

    private bool ZonesEditable => IsActiveMap && Idle && ZonesBlockedReason == "";
    private bool CanAddZone() => ZonesEditable;
    private bool CanDeleteZone() => SelectedZone is not null && ZonesEditable;
    /// <summary>Only offered when the kind picked differs from the chosen zone's: otherwise there is nothing to change.</summary>
    private bool CanChangeZoneKind() => SelectedZone is { Kind: { } k } && k != ZoneKind && ZonesEditable;

    private bool FurnitureEditable => IsActiveMap && Idle && FurnitureBlockedReason == "";
    private bool CanAddFurniture() => FurnitureEditable;
    private bool HasFurniture() => SelectedFurniture is not null && FurnitureEditable;

    partial void OnBusyChanged(bool value) => RefreshCommandStates();

    private void RefreshCommandStates()
    {
        RenameMapCommand.NotifyCanExecuteChanged();
        DeleteMapCommand.NotifyCanExecuteChanged();
        SetActiveCommand.NotifyCanExecuteChanged();
        StartMappingCommand.NotifyCanExecuteChanged();
        RenameRoomCommand.NotifyCanExecuteChanged();
        MergeCommand.NotifyCanExecuteChanged();
        StartSplitCommand.NotifyCanExecuteChanged();
        AddZoneCommand.NotifyCanExecuteChanged();
        ChangeZoneKindCommand.NotifyCanExecuteChanged();
        DeleteZoneCommand.NotifyCanExecuteChanged();
        AddFurnitureCommand.NotifyCanExecuteChanged();
        MoveFurnitureCommand.NotifyCanExecuteChanged();
        RotateFurnitureCommand.NotifyCanExecuteChanged();
        DeleteFurnitureCommand.NotifyCanExecuteChanged();
    }

    // ---- Changing tab, and clicks outside the rooms tab --------------------------

    partial void OnLayerChanged(MapLayer value)
    {
        // What was chosen or under way on one tab means nothing on another.
        LeaveModes();
        SelectedRoom = null;
        SelectedZone = null;
        SelectedFurniture = null;
        RebuildScene();
    }

    /// <summary>
    /// A click on the map, wherever it lands, from the window. On the zones and furniture tabs it
    /// chooses what lies under it — the smallest shape, when several overlap — or lets go of the
    /// choice on empty space. The rooms tab has its own handling (<see cref="RoomClickedById"/>).
    /// </summary>
    public void MapClickedAt(MyDyson.Core.Point p)
    {
        if (Busy || InMode) return;
        switch (Layer)
        {
            case MapLayer.Zones:
                SelectedZone = RestrictionZones.Where(z => z.Corners.Count > 2 && MapShapes.Contains(z.Corners, p.X, p.Y))
                    .OrderBy(z => MapShapes.Area(z.Corners)).FirstOrDefault();
                break;
            case MapLayer.Furniture:
                SelectedFurniture = FurnitureItems.Where(f => f.Corners.Count > 2 && MapShapes.Contains(f.Corners, p.X, p.Y))
                    .OrderBy(f => MapShapes.Area(f.Corners)).FirstOrDefault();
                break;
        }
    }

    [RelayCommand]
    private void ClickZone(ManagedZone? zone)
    {
        if (zone is not null && !Busy && !InMode) SelectedZone = ReferenceEquals(SelectedZone, zone) ? null : zone;
    }

    [RelayCommand]
    private void ClickFurniture(ManagedFurniture? piece)
    {
        if (piece is not null && !Busy && !InMode) SelectedFurniture = ReferenceEquals(SelectedFurniture, piece) ? null : piece;
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

    partial void OnSelectedFurnitureChanged(ManagedFurniture? oldValue, ManagedFurniture? newValue)
    {
        if (oldValue is not null) oldValue.IsChosen = false;
        if (newValue is not null) newValue.IsChosen = true;
        RebuildScene();
        RefreshCommandStates();
    }

    // ---- Zone commands -----------------------------------------------------------

    /// <summary>The smallest zone side accepted; a narrower one would be a slip of the mouse rather than a zone.</summary>
    private const double MinimumZoneSide = 0.2;

    [RelayCommand(CanExecute = nameof(CanAddZone))]
    private void AddZone()
    {
        AddingZone = true;
        Hint = $"Cliquez deux coins opposés de la zone « {ZoneKind.Label} ». Échap pour annuler.";
        Status = "";
        RefreshCommandStates();
    }

    /// <summary>Called by the window once both corners of the new zone have been clicked on the map.</summary>
    public async Task ZoneDrawnAsync(MyDyson.Core.Point a, MyDyson.Core.Point b)
    {
        var kind = ZoneKind;
        LeaveModes();
        if (Math.Abs(a.X - b.X) < MinimumZoneSide || Math.Abs(a.Y - b.Y) < MinimumZoneSide)
        {
            Status = "Zone trop étroite : il faut au moins 20 cm de côté.";
            return;
        }
        var zones = CurrentZones();
        zones.Add(new RestrictionZone(kind, MapShapes.Rectangle(a, b)));
        await SendZonesAsync($"zone « {kind.Label} » ajoutée", zones);
    }

    [RelayCommand(CanExecute = nameof(CanChangeZoneKind))]
    private async Task ChangeZoneKindAsync()
    {
        if (SelectedZone is not { } chosen) return;
        var kind = ZoneKind;
        var zones = RestrictionZones.Select(z => new RestrictionZone(ReferenceEquals(z, chosen) ? kind : z.Kind!, z.Corners)).ToList();
        await SendZonesAsync($"zone changée en « {kind.Label} »", zones);
    }

    [RelayCommand(CanExecute = nameof(CanDeleteZone))]
    private async Task DeleteZoneAsync()
    {
        if (SelectedZone is not { } chosen) return;
        if (Confirm?.Invoke("Supprimer la zone", $"Supprimer la zone « {chosen.Label} » ({chosen.SizeText}) ?") != true) return;
        var zones = RestrictionZones.Where(z => !ReferenceEquals(z, chosen)).Select(z => new RestrictionZone(z.Kind!, z.Corners)).ToList();
        await SendZonesAsync($"zone « {chosen.Label} » supprimée", zones);
    }

    private List<RestrictionZone> CurrentZones() => [.. RestrictionZones.Select(z => new RestrictionZone(z.Kind!, z.Corners))];

    private async Task SendZonesAsync(string label, IReadOnlyList<RestrictionZone> zones)
    {
        if (SelectedMap is not { } map || !TryMapId(map, out var mapId)) return;
        await EditAsync(label, c => c.SetRestrictionsAsync(mapId, zones, _hub.Ct), refused: "Le robot a refusé les zones.");
    }

    // ---- Furniture commands --------------------------------------------------------

    [RelayCommand(CanExecute = nameof(CanAddFurniture))]
    private void AddFurniture()
    {
        PlacingFurniture = true;
        Hint = $"Cliquez l'endroit où poser « {FurnitureKind.Label} » (le centre du meuble). Échap pour annuler.";
        Status = "";
        RefreshCommandStates();
    }

    [RelayCommand(CanExecute = nameof(HasFurniture))]
    private void MoveFurniture()
    {
        MovingFurniture = true;
        Hint = $"Cliquez le nouvel emplacement de « {SelectedFurniture?.Label} » (son centre). Échap pour annuler.";
        Status = "";
        RefreshCommandStates();
    }

    /// <summary>Called by the window with the point clicked while placing or moving a piece.</summary>
    public async Task FurniturePointPickedAsync(MyDyson.Core.Point p)
    {
        var (placing, moving, kind, chosen) = (PlacingFurniture, MovingFurniture, FurnitureKind, SelectedFurniture);
        LeaveModes();
        if (placing)
        {
            var pieces = CurrentFurniture();
            var index = pieces.Count == 0 ? 1 : pieces.Max(f => f.Index) + 1;
            pieces.Add(new FurniturePiece(index, kind, MapShapes.Centred(p, kind.Length, kind.Width)));
            await SendFurnitureAsync($"« {kind.Label} » ajouté", pieces);
        }
        else if (moving && chosen is not null)
        {
            var c = MapShapes.Centre(chosen.Corners);
            await SendFurnitureAsync($"« {chosen.Label} » déplacé",
                ReplaceCorners(chosen, MapShapes.Translate(chosen.Corners, p.X - c.X, p.Y - c.Y)));
        }
    }

    [RelayCommand(CanExecute = nameof(HasFurniture))]
    private async Task RotateFurnitureAsync()
    {
        if (SelectedFurniture is not { } chosen) return;
        await SendFurnitureAsync($"« {chosen.Label} » tourné", ReplaceCorners(chosen, MapShapes.RotateClockwise(chosen.Corners)));
    }

    [RelayCommand(CanExecute = nameof(HasFurniture))]
    private async Task DeleteFurnitureAsync()
    {
        if (SelectedFurniture is not { } chosen) return;
        if (Confirm?.Invoke("Supprimer le meuble", $"Retirer « {chosen.Label} » de la carte ?") != true) return;
        await SendFurnitureAsync($"« {chosen.Label} » retiré", CurrentFurniture().Where(f => f.Index != chosen.Index).ToList());
    }

    private List<FurniturePiece> CurrentFurniture() => [.. FurnitureItems.Select(f => new FurniturePiece(f.Index, f.Kind!, f.Corners))];

    private List<FurniturePiece> ReplaceCorners(ManagedFurniture chosen, IReadOnlyList<MyDyson.Core.Point> corners) =>
        [.. CurrentFurniture().Select(f => f.Index == chosen.Index ? f with { Corners = corners } : f)];

    private Task SendFurnitureAsync(string label, IReadOnlyList<FurniturePiece> pieces) =>
        EditAsync(label, c => c.AdjustFurnitureAsync(pieces, ct: _hub.Ct), refused: "Le robot a refusé les meubles.");

    // ---- Map commands ------------------------------------------------------------

    [RelayCommand(CanExecute = nameof(CanSetActive))]
    private async Task SetActiveAsync()
    {
        if (SelectedMap is not { } map || map.Metadata.IsCurrentMap) return;
        if (!TryMapId(map, out var mapId)) return;
        await EditAsync($"carte active : {map.Metadata.Name}", c => c.ActivateMapAsync(mapId, _hub.Ct));
    }

    [RelayCommand(CanExecute = nameof(HasMap))]
    private async Task RenameMapAsync()
    {
        if (SelectedMap is not { } map || AskForText is null) return;
        var name = AskForText("Renommer la carte", "Nom de la carte :", map.Metadata.Name ?? "");
        if (string.IsNullOrWhiteSpace(name) || name == map.Metadata.Name) return;
        if (!TryMapId(map, out var mapId)) return;

        await EditAsync($"carte renommée en « {name.Trim()} »", c => c.RenameMapAsync(mapId, name.Trim(), _hub.Ct));
    }

    [RelayCommand(CanExecute = nameof(HasMap))]
    private async Task DeleteMapAsync()
    {
        if (SelectedMap is not { } map || !TryMapId(map, out var mapId)) return;
        var name = map.Metadata.Name ?? map.Id;
        var active = map.Metadata.IsCurrentMap
            ? "\n\nC'est la carte active : le robot en choisira une autre de lui-même."
            : "";
        if (Confirm?.Invoke("Supprimer la carte",
                $"Supprimer définitivement la carte « {name} », avec ses pièces et leurs réglages ?{active}\n\nCette action ne peut pas être annulée.") != true) return;

        // Reloaded without a preference: the deleted map is gone, so the list falls back on the
        // active one, which the robot may just have changed.
        await EditAsync($"carte « {name} » supprimée", c => c.DeleteMapAsync(mapId, _hub.Ct), reloadOn: null);
    }

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task StartMappingAsync()
    {
        if (Confirm?.Invoke("Nouvelle carte",
                "Le robot va parcourir le logement pour le cartographier, sans nettoyer. " +
                "Dégagez le sol et ouvrez les portes des pièces à inclure.\n\nLancer la cartographie ?") != true) return;

        Busy = true;
        Status = "Cartographie lancée ; le robot explore le logement.";
        try
        {
            await _hub.RunAsync("cartographie lancée", c => c.StartMappingAsync("fr-CH", _hub.Ct));
        }
        finally { Busy = false; }
    }

    // ---- Room commands -----------------------------------------------------------

    [RelayCommand(CanExecute = nameof(HasRoom))]
    private async Task RenameRoomAsync()
    {
        if (SelectedRoom is not { } room || SelectedMap is not { } map || AskForRoomName is null) return;
        if (AskForRoomName(room, RoomTypes) is not { } answer) return;
        if (string.IsNullOrWhiteSpace(answer.Name)) return;
        if (!TryMapId(map, out var mapId) || room.NumericId < 0) return;

        // A free name travels as type "custom", which is what the app sends too.
        await EditAsync($"pièce renommée en « {answer.Name.Trim()} »",
            c => c.RenameRoomAsync(mapId, room.NumericId, answer.Name.Trim(), answer.Type ?? "custom", _hub.Ct));
    }

    /// <summary>
    /// First press: start gathering rooms (the chosen one, if any, is the first). Second press:
    /// merge what was gathered. The label says which press this is.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanMerge))]
    private async Task MergeAsync()
    {
        if (!Merging)
        {
            Merging = true;
            if (SelectedRoom is { } first)
            {
                SelectedRoom = null;
                _mergeSet.Add(first);
                first.IsChosen = true;
            }
            Hint = "Cliquez les pièces à fusionner, sur la carte ou dans la liste. « Fusionner » à nouveau pour valider, Échap pour annuler.";
            UpdateMergeLabel();
            RebuildScene();
            return;
        }

        if (SelectedMap is not { } map || !TryMapId(map, out var mapId)) return;
        var rooms = _mergeSet.Where(r => r.NumericId >= 0).ToList();
        if (rooms.Count < 2) { Status = "Choisissez au moins deux pièces à fusionner."; return; }
        if (Confirm?.Invoke("Fusionner les pièces",
                $"Fusionner {string.Join(", ", rooms.Select(p => p.DisplayName))} en une seule pièce ?") != true) return;

        LeaveModes();
        await EditAsync($"{rooms.Count} pièces fusionnées", c => c.MergeRoomsAsync(mapId, rooms.Select(p => p.NumericId), ct: _hub.Ct),
            refused: "Le robot a refusé la fusion : les pièces doivent se toucher.");
    }

    private void UpdateMergeLabel()
    {
        MergeButtonLabel = !Merging ? "Fusionner des pièces…"
            : _mergeSet.Count < 2 ? "Fusionner (choisissez 2 pièces ou plus)"
            : $"Fusionner les {_mergeSet.Count} pièces";
        MergeCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(HasRoom))]
    private void StartSplit()
    {
        if (SelectedRoom is null) { Status = "Choisissez d'abord la pièce à diviser."; return; }
        Splitting = true;
        Hint = "Cliquez les deux extrémités du trait de coupe. Échap pour annuler.";
        Status = "";
        RefreshCommandStates();
    }

    /// <summary>Called by the window once both ends of the cut have been clicked on the map.</summary>
    public async Task SplitAsync(MyDyson.Core.Point from, MyDyson.Core.Point to)
    {
        var room = SelectedRoom;
        LeaveModes();
        if (room is null || SelectedMap is not { } map) return;
        if (!TryMapId(map, out var mapId) || room.NumericId < 0) return;

        // A cut that misses the room, or that the robot will not make, comes back refused.
        await EditAsync($"pièce {room.DisplayName} divisée", c => c.SplitRoomAsync(mapId, room.NumericId, from, to, ct: _hub.Ct),
            refused: "Le robot a refusé la division : la pièce est sans doute trop petite à cet endroit, ou le trait ne la traverse pas.");
    }

    /// <summary>Escape, or the cancel button: leaves splitting or merging without doing anything.</summary>
    [RelayCommand]
    private void Cancel()
    {
        var wasMerging = Merging;
        LeaveModes();
        if (wasMerging) RebuildScene();
    }

    private void LeaveModes()
    {
        Splitting = false;
        if (Merging)
        {
            foreach (var r in _mergeSet) r.IsChosen = false;
            _mergeSet.Clear();
            Merging = false;
        }
        AddingZone = false;
        PlacingFurniture = false;
        MovingFurniture = false;
        Hint = LayerHint;
        UpdateMergeLabel();
        RefreshCommandStates();
    }

    // ---- Sending -------------------------------------------------------------------

    /// <summary>
    /// Sends one map edit, waits for the cloud copy to catch up, then re-reads the maps — on the
    /// map given by <paramref name="reloadOn"/>, which defaults to the one on screen.
    /// </summary>
    private async Task EditAsync(string label, Func<RobotMqttClient, Task<MapEditResult?>> edit, string? reloadOn = "", string? refused = null)
    {
        if (_hub.Session?.Client is not { IsConnected: true } client) { Status = "Robot non connecté."; return; }
        Busy = true;
        Status = "";
        try
        {
            var uploaded = WatchForMapUploadAsync();
            var result = await edit(client);
            if (result is null)
            {
                Status = refused ?? "Le robot a refusé la modification.";
                _hub.AddLog($"{label} : refusé par le robot");
                return;
            }
            _hub.AddLog(label);
            // The robot answers as soon as it has re-saved the map, but the cloud copy the REST API
            // serves catches up a moment later and announces itself with MAP-UPLOAD-STATUS. Reading
            // the map back before that returns the old one, which is what made a rename look as if
            // it had not happened.
            await uploaded;
            await LoadAsync(reloadOn == "" ? SelectedMap?.Id : reloadOn);
            Status = char.ToUpper(label[0], CultureInfo.CurrentCulture) + label[1..] + ".";
            Changed?.Invoke();
        }
        catch (OperationCanceledException) when (_hub.IsShuttingDown) { }
        catch (Exception ex)
        {
            Status = ex.Message;
            _hub.AddLog($"{label} : {ex.Message}");
        }
        finally { Busy = false; }
    }

    /// <summary>
    /// Completes on the next MAP-UPLOAD-STATUS, or after a few seconds if none arrives — every kind
    /// of edit was followed by one in the captures, but an edit the robot decides is a no-op may
    /// well not re-upload anything, and waiting forever for that would hang the window.
    /// </summary>
    private async Task WatchForMapUploadAsync()
    {
        if (_hub.Session?.Tracker is not { } tracker) return;
        var uploaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnEvent(string name, System.Text.Json.Nodes.JsonObject _)
        {
            if (name == "MAP-UPLOAD-STATUS") uploaded.TrySetResult();
        }
        tracker.EventReceived += OnEvent;
        try
        {
            await Task.WhenAny(uploaded.Task, Task.Delay(TimeSpan.FromSeconds(8), _hub.Ct));
            // The cloud copy lands a moment after the announcement, not with it.
            await Task.Delay(TimeSpan.FromMilliseconds(800), _hub.Ct);
        }
        finally { tracker.EventReceived -= OnEvent; }
    }

    private bool TryMapId(MapItem map, out long id)
    {
        if (long.TryParse(map.Id, NumberStyles.Integer, CultureInfo.InvariantCulture, out id)) return true;
        Status = $"Identifiant de carte inattendu : {map.Id}";
        return false;
    }
}
