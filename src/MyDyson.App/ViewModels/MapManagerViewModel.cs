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

/// <summary>One room of the map being managed, with the tick used for merging.</summary>
public sealed partial class ManagedRoom(MapZone Zone) : ObservableObject
{
    public MapZone Zone { get; } = Zone;
    public string Id => Zone.Id;
    public int NumericId => int.TryParse(Zone.Id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : -1;
    public string DisplayName => RoomTypeLabels.Resolve(Zone.Type, Zone.Name, Zone.Id);
    public string AreaText => Zone.Area is { } a ? $"{a:F1} m²" : "";
    /// <summary>The type, shown beside the name only when it adds something: a typed room called
    /// after its own type would otherwise read "Chambre Chambre".</summary>
    public string TypeText
    {
        get
        {
            var label = RoomTypeLabels.DefaultNameFor(Zone.Type);
            if (label is null) return "personnalisée";
            return label == DisplayName ? "" : label.ToLower(System.Globalization.CultureInfo.CurrentCulture);
        }
    }

    [ObservableProperty] private bool _picked;
}

/// <summary>
/// The Gérer les cartes window: rename a map or a room, merge rooms, cut a room in two, make a map
/// active, and run a new mapping scan. It works off its own copy of the map rather than the
/// dashboard's, since every edit makes the robot re-save the map and the result has to be re-read.
///
/// What the robot does not offer, and so is absent here: deleting a map, deleting a room, and
/// setting a room's outline. Splitting only ever cuts along a straight line, and the robot snaps
/// that line to its own occupancy grid, so a room's shape can be steered but not dictated.
/// </summary>
public sealed partial class MapManagerViewModel : ObservableObject
{
    private readonly RobotHub _hub;
    private readonly DisplaySettings _display;

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

    // ---- Splitting -------------------------------------------------------------

    /// <summary>True while the user is aiming a cut on the map; the view turns clicks into points.</summary>
    [ObservableProperty] private bool _splitting;
    [ObservableProperty] private string _splitHint = "";

    /// <summary>Asks the window to show a text prompt; returns null when the user cancels.</summary>
    public Func<string, string, string, string?>? AskForText { get; set; }
    /// <summary>Asks the window for a room name and type; returns null when the user cancels.</summary>
    public Func<ManagedRoom, IReadOnlyList<RoomTypeOption>, (string Name, string? Type)?>? AskForRoomName { get; set; }
    /// <summary>Asks the window for a yes/no confirmation.</summary>
    public Func<string, string, bool>? Confirm { get; set; }

    public async Task LoadAsync(string? preferredMapId = null)
    {
        try
        {
            var wanted = preferredMapId ?? SelectedMap?.Id;
            var metadata = await _hub.Api.GetMapMetadataAsync(_hub.Serial, _hub.Ct);
            Maps.Clear();
            foreach (var m in metadata) Maps.Add(new MapItem(m));
            SelectedMap = Maps.FirstOrDefault(m => m.Id == wanted)
                ?? Maps.FirstOrDefault(m => m.Metadata.IsCurrentMap)
                ?? Maps.FirstOrDefault();
        }
        catch (OperationCanceledException) when (_hub.IsShuttingDown) { }
        catch (Exception ex) { Status = $"Cartes : {ex.Message}"; }
    }

    partial void OnSelectedMapChanged(MapItem? value)
    {
        Splitting = false;
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
            _map = map;
            _grid = grid;
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
        // The room being renamed or cut is highlighted, the others dimmed, so it is obvious which
        // one an action is about to touch.
        SelectedZoneIds = SelectedRoom is { } r ? new HashSet<string>([r.Id], StringComparer.Ordinal) : null,
        ShowFurniture = _display.ShowFurniture,
        ShowTravelPath = _display.ShowTravelPath,
    };

    partial void OnSelectedRoomChanged(ManagedRoom? value) => RebuildScene();

    /// <summary>Called by the window when a room is clicked on the map.</summary>
    public void SelectRoomById(string zoneId) => SelectedRoom = Rooms.FirstOrDefault(r => r.Id == zoneId) ?? SelectedRoom;

    // ---- Commands --------------------------------------------------------------

    [RelayCommand]
    private async Task RenameMapAsync()
    {
        if (SelectedMap is not { } map || AskForText is null) return;
        var name = AskForText("Renommer la carte", "Nom de la carte :", map.Metadata.Name ?? "");
        if (string.IsNullOrWhiteSpace(name) || name == map.Metadata.Name) return;
        if (!TryMapId(map, out var mapId)) return;

        await EditAsync($"carte renommée en « {name} »", c => c.RenameMapAsync(mapId, name.Trim(), _hub.Ct));
    }

    [RelayCommand]
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

    [RelayCommand]
    private async Task MergeRoomsAsync()
    {
        if (SelectedMap is not { } map) return;
        var picked = Rooms.Where(r => r.Picked && r.NumericId >= 0).ToList();
        if (picked.Count < 2) { Status = "Cochez au moins deux pièces à fusionner."; return; }
        if (!TryMapId(map, out var mapId)) return;
        if (Confirm?.Invoke("Fusionner les pièces",
                $"Fusionner {string.Join(", ", picked.Select(p => p.DisplayName))} en une seule pièce ?") != true) return;

        await EditAsync($"{picked.Count} pièces fusionnées", c => c.MergeRoomsAsync(mapId, picked.Select(p => p.NumericId), ct: _hub.Ct));
    }

    [RelayCommand]
    private void StartSplit()
    {
        if (SelectedRoom is null) { Status = "Choisissez d'abord la pièce à diviser."; return; }
        Splitting = true;
        SplitHint = "Cliquez les deux extrémités du trait de coupe. Échap pour annuler.";
        Status = "";
    }

    [RelayCommand]
    private void CancelSplit()
    {
        Splitting = false;
        SplitHint = "";
    }

    /// <summary>Called by the window once both ends of the cut have been clicked on the map.</summary>
    public async Task SplitAsync(MyDyson.Core.Point from, MyDyson.Core.Point to)
    {
        Splitting = false;
        SplitHint = "";
        if (SelectedRoom is not { } room || SelectedMap is not { } map) return;
        if (!TryMapId(map, out var mapId) || room.NumericId < 0) return;

        // Said out loud because the robot rarely cuts exactly where asked: it snaps the line to its
        // own occupancy grid, and a line that misses the room is simply refused.
        await EditAsync($"pièce {room.DisplayName} divisée", c => c.SplitRoomAsync(mapId, room.NumericId, from, to, ct: _hub.Ct));
    }

    [RelayCommand]
    private async Task SetActiveAsync()
    {
        if (SelectedMap is not { } map || map.Metadata.IsCurrentMap) return;
        if (!TryMapId(map, out var mapId)) return;
        await EditAsync($"carte active : {map.Metadata.Name}", async c =>
        {
            await c.SetCurrentMapAsync(mapId, _hub.Ct);
            return new MapEditResult(mapId, 0, 0);
        });
    }

    [RelayCommand]
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

    /// <summary>
    /// Sends one map edit, then re-reads the map list. The robot answers as soon as it has re-saved
    /// the map, but the cloud copy the REST API serves catches up a moment later (it announces
    /// itself with MAP-UPLOAD-STATUS), hence the short wait before reloading.
    /// </summary>
    private async Task EditAsync(string label, Func<RobotMqttClient, Task<MapEditResult?>> edit)
    {
        if (_hub.Session?.Client is not { IsConnected: true } client) { Status = "Robot non connecté."; return; }
        Busy = true;
        Status = "";
        try
        {
            var result = await edit(client);
            if (result is null)
            {
                Status = "Le robot a refusé la modification.";
                _hub.AddLog($"{label} : refusé par le robot");
                return;
            }
            _hub.AddLog(label);
            await Task.Delay(TimeSpan.FromSeconds(2), _hub.Ct);
            await LoadAsync(SelectedMap?.Id);
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

    /// <summary>Raised after a successful edit so the dashboard can pick up the new names.</summary>
    public event Action? Changed;

    private bool TryMapId(MapItem map, out long id)
    {
        if (long.TryParse(map.Id, NumberStyles.Integer, CultureInfo.InvariantCulture, out id)) return true;
        Status = $"Identifiant de carte inattendu : {map.Id}";
        return false;
    }
}
