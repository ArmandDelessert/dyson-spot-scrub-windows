using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DyssCockpit.Presentation.Map;
using DyssCockpit.Presentation.Services;
using DyssCockpit.Core;

using static DyssCockpit.Core.Translation;

namespace DyssCockpit.Presentation.ViewModels;

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
    private readonly AppSettings _settings;
    /// <summary>The rooms gathered for a merge, in click order: the robot receives them in that order.</summary>
    private readonly List<ManagedRoom> _mergeSet = [];

    public MapManagerViewModel(RobotHub hub, AppSettings settings)
    {
        _hub = hub;
        _settings = settings;
        RoomTypes = [new RoomTypeOption(null, T("Personnalisée (nom libre)", "Custom (free name)")),
                     .. RoomTypeLabels.All.Select(t => new RoomTypeOption(t.Type, t.Label))];

        Canvas = new MapInteraction
        {
            // Zones, drawn or resized, never shrink below what is accepted.
            MinimumShapeSide = MinimumZoneSide,
            // Cuts, and zones and furniture when the box says so, land on the robot's 5 cm grid.
            SnapToGrid = GridActive,
            ShowPointerCoordinates = _settings.ShowPointerCoordinates,
            ShowShapeDimensions = _settings.ShowShapeDimensions,
        };
        // Every click comes here, which knows which tab it is for.
        Canvas.WorldClicked += MapClickedAt;
        Canvas.ClickConfirmed += MapClickConfirmedAt;
        Canvas.ZoneClicked += RoomClickedById;
        Canvas.EmptySpaceClicked += ClearRoomSelection;
        Canvas.LinePicked += (from, to) => _ = SplitAsync(from, to);
        Canvas.RectanglePicked += (a, b) => _ = ZoneDrawnAsync(a, b);
        Canvas.PointPicked += p => _ = FurniturePointPickedAsync(p);
        Canvas.ShapeEdited += corners => _ = ShapeDroppedAsync(corners);
        // The map owns the picking gestures; this only says which one is on.
        PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(Splitting) or nameof(AddingZone) or nameof(PlacingFurniture):
                    Canvas.Picking = Splitting ? MapPick.Line
                        : AddingZone ? MapPick.Rectangle
                        : PlacingFurniture ? MapPick.Point
                        : MapPick.None;
                    break;
                case nameof(PlacementShape):
                    Canvas.PlacementShape = PlacementShape;
                    break;
                case nameof(GridActive):
                    Canvas.SnapToGrid = GridActive;
                    break;
                // Raised together, on every change of choice, tab or state that could affect them.
                case nameof(EditableShape):
                    if (!ReferenceEquals(Canvas.EditableShape, EditableShape)) Canvas.EditableShape = EditableShape;
                    Canvas.EditableShapeResizable = EditableShapeResizable;
                    break;
                case nameof(Scene):
                    Canvas.Scene = Scene;
                    break;
            }
        };
    }

    /// <summary>The map being edited: zoom, pan, and what clicks and drags on it mean.</summary>
    public MapInteraction Canvas { get; }

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
    [ObservableProperty, NotifyPropertyChangedFor(nameof(InMode)), NotifyPropertyChangedFor(nameof(GridActive))] private bool _splitting;
    /// <summary>True while clicks gather rooms to merge rather than choose one.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(InMode))] private bool _merging;
    /// <summary>True while the user is drawing a restriction zone's rectangle on the map.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(InMode))] private bool _addingZone;
    /// <summary>True while the user is choosing where a new piece of furniture goes.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(InMode)), NotifyPropertyChangedFor(nameof(PlacementShape))] private bool _placingFurniture;
    /// <summary>An edit is being aimed or gathered, which is when there is something to cancel.</summary>
    public bool InMode => Merging || Splitting || AddingZone || PlacingFurniture;
    /// <summary>What the current mode expects from the user, shown above the map.</summary>
    [ObservableProperty] private string _hint = RoomsHint;
    [ObservableProperty] private string _mergeButtonLabel = T("Fusionner des pièces…", "Merge rooms…");

    private static string RoomsHint => T("Cliquez une pièce pour la choisir, en dehors pour effacer le choix. Molette pour zoomer, glisser pour déplacer.", "Click a room to choose it, outside to clear the choice. Wheel to zoom, drag to move.");
    private static string ZonesHint => T("Cliquez une zone pour la choisir, puis faites-la glisser pour la déplacer ou tirez un de ses coins pour la redimensionner. Molette pour zoomer, glisser ailleurs pour déplacer la carte.", "Click a zone to choose it, then drag it to move it or pull one of its corners to resize it. Wheel to zoom, drag elsewhere to move the map.");
    private static string FurnitureHint => T("Cliquez un meuble pour le choisir, puis faites-le glisser pour le déplacer. Molette pour zoomer, glisser ailleurs pour déplacer la carte.", "Click a piece of furniture to choose it, then drag it to move it. Wheel to zoom, drag elsewhere to move the map.");

    private string LayerHint => Layer switch
    {
        MapLayer.Zones => ZonesHint,
        MapLayer.Furniture => FurnitureHint,
        _ => RoomsHint,
    };

    // ---- Tabs, and what the map lets the user drag ------------------------------

    /// <summary>The tab on show; the TabControl binds <see cref="LayerIndex"/>.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(LayerIndex))] private MapLayer _layer;
    public int LayerIndex
    {
        get => (int)Layer;
        set => Layer = (MapLayer)value;
    }

    /// <summary>
    /// What the map lets the user drag: the chosen zone (with corner handles) or piece of furniture,
    /// when it can be changed and nothing else is under way. Handed to <see cref="Canvas"/>.
    /// </summary>
    public IReadOnlyList<DyssCockpit.Core.Point>? EditableShape => Layer switch
    {
        MapLayer.Zones when SelectedZone is { } z && CanDeleteZone() => z.Corners,
        MapLayer.Furniture when SelectedFurniture is { } f && HasFurniture() => f.Corners,
        _ => null,
    };
    public bool EditableShapeResizable => Layer == MapLayer.Zones;

    /// <summary>
    /// The box of the Zones and Meubles tabs: whether zones and furniture land on the robot's 5 cm
    /// grid when drawn, placed or dragged, with the grid shown. Kept from one run to the next.
    /// </summary>
    public bool SnapToGrid
    {
        get => _settings.SnapToGrid;
        set
        {
            if (_settings.SnapToGrid == value) return;
            _settings.SnapToGrid = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(GridActive));
        }
    }

    /// <summary>
    /// What the map snaps to right now. A cut always does, whatever the box says: the robot redraws
    /// the boundary along its cells anyway, so the grid only shows where it will fall.
    /// </summary>
    public bool GridActive => Splitting || SnapToGrid;


    /// <summary>"1,2 × 0,5 m": the first two sides of a shape.</summary>
    internal static string SizeOf(IReadOnlyList<DyssCockpit.Core.Point> corners)
    {
        var (a, b) = MapShapes.Sides(corners);
        return string.Create(CultureInfo.CurrentCulture, $"{a:0.0} × {b:0.0} m");
    }

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
        catch (Exception ex) { Status = T($"Cartes : {ex.Message}", $"Maps: {ex.Message}"); }
    }

    // ---- Map orientation ----------------------------------------------------------

    /// <summary>The four ways a map can be shown, clockwise, as the phone's rotate button steps through them.</summary>
    public IReadOnlyList<OrientationOption> MapOrientations { get; } =
    [
        new(0, T("0° (d'origine)", "0° (original)")),
        new(1, T("90° (quart de tour horaire)", "90° (quarter turn clockwise)")),
        new(2, T("180° (demi-tour)", "180° (half turn)")),
        new(3, T("270° (quart de tour antihoraire)", "270° (quarter turn anticlockwise)")),
    ];

    /// <summary>
    /// How the map on screen is turned. Picking another one saves it on the cloud at once, for this
    /// app and the phone alike; set back quietly whenever a map is loaded, so only picks are sent.
    /// Any map can be turned, the active one or not: it is a display setting, the robot is not told.
    /// </summary>
    [ObservableProperty] private OrientationOption? _mapOrientation;
    private bool _showingMapOrientation;

    public bool CanRotateMap => SelectedMap is not null && _map?.Id == SelectedMap.Id && Idle;

    private void ShowMapOrientation()
    {
        _showingMapOrientation = true;
        MapOrientation = _map is null ? null : MapOrientations[(_map.Orientation ?? 0) / 90 % 4];
        _showingMapOrientation = false;
    }

    partial void OnMapOrientationChanged(OrientationOption? value)
    {
        if (_showingMapOrientation || value is null || SelectedMap is not { } map || _map?.Id != map.Id) return;
        if (value.QuarterTurns * 90 == (_map.Orientation ?? 0)) return;
        _ = RotateMapAsync(map, value.QuarterTurns * 90);
    }

    private async Task RotateMapAsync(MapItem map, int degrees)
    {
        Busy = true;
        Status = "";
        try
        {
            await _hub.Api.SetMapOrientationAsync(_hub.Serial, map.Id, degrees, _hub.Ct);
            _hub.AddLog(T($"carte « {map.Metadata.Name} » tournée à {degrees}°", $"map “{map.Metadata.Name}” turned to {degrees}°"));
            Status = T($"Carte tournée à {degrees}°.", $"Map turned to {degrees}°.");
            await LoadMapAsync(map);
            Changed?.Invoke();
        }
        catch (OperationCanceledException) when (_hub.IsShuttingDown) { }
        catch (Exception ex)
        {
            Status = T($"Rotation impossible : {ex.Message}", $"Rotation failed: {ex.Message}");
            _hub.AddLog(T($"rotation de la carte : {ex.Message}", $"map rotation: {ex.Message}"));
        }
        finally
        {
            Busy = false;
            // Refused or not: the list goes back to how the map really stands.
            ShowMapOrientation();
        }
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
        if (value is not null) MapLoading = LoadMapAsync(value);
    }

    /// <summary>The loading of the map last chosen, done or under way: what a test waits for, rather than looking again and again.</summary>
    internal Task MapLoading { get; private set; } = Task.CompletedTask;

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
            // Listed by name; each remembers its place in the cloud's list, the order they go back in.
            var byName = StringComparer.Create(CultureInfo.CurrentCulture, ignoreCase: true);
            RestrictionZones.Clear();
            foreach (var z in (map.Restrictions ?? []).Select((r, i) => new ManagedZone(r, i))
                         .OrderBy(z => z.Label, byName).ThenByDescending(z => MapShapes.Area(z.Corners)))
                RestrictionZones.Add(z);
            FurnitureItems.Clear();
            foreach (var f in (map.Furniture ?? []).Select(f => new ManagedFurniture(f))
                         .OrderBy(f => f.Label, byName).ThenBy(f => f.Index))
                FurnitureItems.Add(f);
            OnPropertyChanged(nameof(ZonesBlockedReason));
            OnPropertyChanged(nameof(FurnitureBlockedReason));
            OnPropertyChanged(nameof(ZonesEmptyText));
            OnPropertyChanged(nameof(FurnitureEmptyText));
            _map = map;
            ShowMapOrientation();
            _grid = grid;
            RefreshCommandStates();
            RebuildScene();
        }
        catch (OperationCanceledException) when (_hub.IsShuttingDown) { }
        catch (Exception ex) { Status = T($"Carte {item.Id} : {ex.Message}", $"Map {item.Id}: {ex.Message}"); }
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
        ShowFurniture = _settings.ShowFurniture || Layer == MapLayer.Furniture,
        ShowTravelPath = _settings.ShowTravelPath,
        ShowCleanedArea = _settings.ShowCleanedArea,
    };

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
        : T("Seule la carte active peut être modifiée. Définissez celle-ci comme active pour la renommer, la supprimer ou changer ses pièces, ses zones ou ses meubles.", "Only the active map can be changed. Make this one active to rename it, delete it, or change its rooms, zones or furniture.");

    private bool Idle => !Busy && !InMode;
    private bool NotBusy() => Idle;
    private bool HasMap() => SelectedMap is not null && IsActiveMap && Idle;
    private bool HasRoom() => SelectedRoom is not null && IsActiveMap && Idle;
    private bool CanSetActive() => SelectedMap is { Metadata.IsCurrentMap: false } && Idle;
    /// <summary>Entering merge mode needs two rooms to exist; confirming it needs two gathered.</summary>
    private bool CanMerge() => !Busy && (Merging || !InMode) && IsActiveMap && (Merging ? _mergeSet.Count >= 2 : Rooms.Count >= 2);

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
        DeleteFurnitureCommand.NotifyCanExecuteChanged();
        // Not commands, but gated by the same conditions.
        OnPropertyChanged(nameof(CanOrientFurniture));
        OnPropertyChanged(nameof(CanRotateMap));
        OnPropertyChanged(nameof(EditableShape));
        OnPropertyChanged(nameof(EditableShapeResizable));
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
    /// A click on the map, wherever it lands, from <see cref="Canvas"/>. On the zones and furniture
    /// tabs it chooses what lies under it at once — the smallest shape, when several overlap. Letting
    /// go of the choice, on a click beside every shape, waits for <see cref="MapClickConfirmedAt"/>,
    /// as a double click there only means to reset the zoom. The rooms tab has its own handling
    /// (<see cref="RoomClickedById"/>).
    /// </summary>
    public void MapClickedAt(DyssCockpit.Core.Point p)
    {
        if (Busy || InMode) return;
        switch (Layer)
        {
            case MapLayer.Zones when ZoneAt(p) is { } zone:
                SelectedZone = zone;
                break;
            case MapLayer.Furniture when FurnitureAt(p) is { } piece:
                SelectedFurniture = piece;
                break;
        }
    }

    /// <summary>A single click confirmed not to be the first half of a double: beside every shape, it lets go of the chosen one.</summary>
    public void MapClickConfirmedAt(DyssCockpit.Core.Point p)
    {
        if (Busy || InMode) return;
        if (Layer == MapLayer.Zones && ZoneAt(p) is null) SelectedZone = null;
        else if (Layer == MapLayer.Furniture && FurnitureAt(p) is null) SelectedFurniture = null;
    }

    /// <summary>
    /// Called by the map when the chosen zone or piece has been dragged and dropped: moved, or —
    /// for a zone — resized by a corner. A zone left narrower than <see cref="MinimumZoneSide"/> is
    /// refused rather than sent.
    /// </summary>
    public async Task ShapeDroppedAsync(IReadOnlyList<DyssCockpit.Core.Point> corners)
    {
        if (Busy || InMode || corners.Count != 4) return;
        if (Layer == MapLayer.Zones && SelectedZone is { } zone && CanDeleteZone())
        {
            var (first, second) = MapShapes.Sides(corners);
            if (first < MinimumZoneSide - MapShapes.Tolerance || second < MinimumZoneSide - MapShapes.Tolerance)
            {
                Status = T("Zone trop étroite : il faut au moins 20 cm de côté.", "Zone too narrow: each side needs at least 20 cm.");
                return;
            }
            var zones = OrderedZones().Select(z => new RestrictionZone(z.Kind!, ReferenceEquals(z, zone) ? corners : z.Corners)).ToList();
            var resized = Math.Abs(MapShapes.Area(corners) - MapShapes.Area(zone.Corners)) > 1e-6;
            await SendZonesAsync(resized ? T($"zone « {zone.Label} » redimensionnée", $"“{zone.Label}” zone resized") : T($"zone « {zone.Label} » déplacée", $"“{zone.Label}” zone moved"), zones);
        }
        else if (Layer == MapLayer.Furniture && SelectedFurniture is { } piece && HasFurniture())
        {
            await SendFurnitureAsync(T($"« {piece.Label} » déplacé", $"“{piece.Label}” moved"), ReplaceCorners(piece, corners));
        }
    }

    // ---- Map commands ------------------------------------------------------------

    [RelayCommand(CanExecute = nameof(CanSetActive))]
    private async Task SetActiveAsync()
    {
        if (SelectedMap is not { } map || map.Metadata.IsCurrentMap) return;
        if (!TryMapId(map, out var mapId)) return;
        await EditAsync(T($"carte active : {map.Metadata.Name}", $"active map: {map.Metadata.Name}"), c => c.ActivateMapAsync(mapId, _hub.Ct));
    }

    [RelayCommand(CanExecute = nameof(HasMap))]
    private async Task RenameMapAsync()
    {
        if (SelectedMap is not { } map) return;
        var name = await _hub.Dialogs.AskTextAsync(T("Renommer la carte", "Rename the map"), T("Nom de la carte :", "Map name:"), map.Metadata.Name ?? "");
        if (string.IsNullOrWhiteSpace(name) || name == map.Metadata.Name) return;
        if (!TryMapId(map, out var mapId)) return;

        await EditAsync(T($"carte renommée en « {name.Trim()} »", $"map renamed to “{name.Trim()}”"), c => c.RenameMapAsync(mapId, name.Trim(), _hub.Ct));
    }

    [RelayCommand(CanExecute = nameof(HasMap))]
    private async Task DeleteMapAsync()
    {
        if (SelectedMap is not { } map || !TryMapId(map, out var mapId)) return;
        var name = map.Metadata.Name ?? map.Id;
        var active = map.Metadata.IsCurrentMap
            ? T("\n\nC'est la carte active : le robot en choisira une autre de lui-même.", "\n\nThis is the active map: the robot will pick another one by itself.")
            : "";
        if (!await _hub.Dialogs.ConfirmAsync(T("Supprimer la carte", "Delete the map"),
                T($"Supprimer définitivement la carte « {name} », avec ses pièces et leurs réglages ?{active}\n\nCette action ne peut pas être annulée.", $"Permanently delete the “{name}” map, with its rooms and their settings?{active}\n\nThis cannot be undone."))) return;

        // Reloaded without a preference: the deleted map is gone, so the list falls back on the
        // active one, which the robot may just have changed.
        await EditAsync(T($"carte « {name} » supprimée", $"“{name}” map deleted"), c => c.DeleteMapAsync(mapId, _hub.Ct), reloadOn: null);
    }

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task StartMappingAsync()
    {
        if (!await _hub.Dialogs.ConfirmAsync(T("Nouvelle carte", "New map"),
                T("Le robot va parcourir le logement pour le cartographier, sans nettoyer. ", "The robot will go round the home to map it, without cleaning. ") +
                T("Dégagez le sol et ouvrez les portes des pièces à inclure.\n\nLancer la cartographie ?", "Clear the floor and open the doors of the rooms to include.\n\nStart mapping?"))) return;

        Busy = true;
        Status = T("Cartographie lancée ; le robot explore le logement.", "Mapping started; the robot is exploring the home.");
        try
        {
            // The map language names the rooms the scan finds: the account's, as the phone sends it.
            await _hub.RunAsync(T("cartographie lancée", "mapping started"), c => c.StartMappingAsync(_hub.Culture, _hub.Ct));
        }
        finally { Busy = false; }
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
        Hint = LayerHint;
        UpdateMergeLabel();
        RefreshCommandStates();
    }

    // ---- Sending -------------------------------------------------------------------

    /// <summary>
    /// Sends one map edit, waits for the cloud copy to catch up, then re-reads the maps — on the
    /// map given by <paramref name="reloadOn"/>, which defaults to the one on screen.
    /// </summary>
    private async Task EditAsync(string label, Func<IRobotCommands, Task<MapEditResult?>> edit, string? reloadOn = "", string? refused = null)
    {
        if (_hub.Robot is not { } client) { Status = T("Robot non connecté.", "Robot not connected."); return; }
        Busy = true;
        Status = "";
        try
        {
            var uploaded = WatchForMapUploadAsync();
            var result = await edit(client);
            if (result is null)
            {
                Status = refused ?? T("Le robot a refusé la modification.", "The robot refused the change.");
                _hub.AddLog(T($"{label} : refusé par le robot", $"{label}: refused by the robot"));
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
            _hub.AddLog(T($"{label} : {ex.Message}", $"{label}: {ex.Message}"));
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
        Status = T($"Identifiant de carte inattendu : {map.Id}", $"Unexpected map id: {map.Id}");
        return false;
    }
}
