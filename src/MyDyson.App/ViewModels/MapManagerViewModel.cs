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
    /// <summary>Splitting or merging is under way, which is when there is something to cancel.</summary>
    public bool InMode => Merging || Splitting;
    /// <summary>What the current mode expects from the user, shown above the map.</summary>
    [ObservableProperty] private string _hint = DefaultHint;
    [ObservableProperty] private string _mergeButtonLabel = "Fusionner des pièces…";

    private const string DefaultHint = "Cliquez une pièce pour la choisir, en dehors pour effacer le choix. Molette pour zoomer, glisser pour déplacer.";

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
        ShowFurniture = _display.ShowFurniture,
        ShowTravelPath = _display.ShowTravelPath,
    };

    // ---- Choosing rooms ----------------------------------------------------------

    /// <summary>A click on a room, from the list or the map. Chooses it, or gathers it for a merge.</summary>
    public void RoomClicked(ManagedRoom room)
    {
        if (Splitting || Busy) return;
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
        if (Splitting) return;
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

    private bool Idle => !Busy && !Splitting && !Merging;
    private bool NotBusy() => Idle;
    private bool HasMap() => SelectedMap is not null && Idle;
    private bool HasRoom() => SelectedRoom is not null && Idle;
    private bool CanSetActive() => SelectedMap is { Metadata.IsCurrentMap: false } && Idle;
    /// <summary>Entering merge mode needs two rooms to exist; confirming it needs two gathered.</summary>
    private bool CanMerge() => !Busy && !Splitting && (Merging ? _mergeSet.Count >= 2 : Rooms.Count >= 2);

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
    }

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
        await EditAsync($"{rooms.Count} pièces fusionnées", c => c.MergeRoomsAsync(mapId, rooms.Select(p => p.NumericId), ct: _hub.Ct));
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
        await EditAsync($"pièce {room.DisplayName} divisée", c => c.SplitRoomAsync(mapId, room.NumericId, from, to, ct: _hub.Ct));
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
        Hint = DefaultHint;
        UpdateMergeLabel();
        RefreshCommandStates();
    }

    // ---- Sending -------------------------------------------------------------------

    /// <summary>
    /// Sends one map edit, waits for the cloud copy to catch up, then re-reads the maps — on the
    /// map given by <paramref name="reloadOn"/>, which defaults to the one on screen.
    /// </summary>
    private async Task EditAsync(string label, Func<RobotMqttClient, Task<MapEditResult?>> edit, string? reloadOn = "")
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
                Status = "Le robot a refusé la modification.";
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
