using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MyDyson.App.Rendering;
using MyDyson.App.Services;
using MyDyson.Core;

namespace MyDyson.App.ViewModels;

/// <summary>
/// The Nettoyage card and the Carte tab: which map, which rooms in which order with which
/// settings, and the live scene (grid, robot, trail, obstacles, stains) drawn from it.
/// </summary>
public sealed partial class CleaningViewModel(RobotHub hub, MapCatalog maps, DisplaySettings display) : ObservableObject, IDisposable
{
    public ObservableCollection<MapItem> Maps => maps.Maps;
    /// <summary>Exposed so the map toolbar can bind straight to it: a Popup sits outside the window's visual tree, where an ancestor lookup would find nothing.</summary>
    public DisplaySettings Display => display;
    [ObservableProperty] private MapItem? _selectedMap;
    public ObservableCollection<ZoneItem> Zones { get; } = new();
    [ObservableProperty] private MapScene _scene = new();
    [ObservableProperty] private bool _canStart;

    private PersistentMap? _map;
    private bool _robotReady;
    private int _nextOrder = 1;
    private RobotPosition? _robotPosition;
    private IReadOnlyList<MyDyson.Core.Point>? _lastPath;
    /// <summary>Grown in real time from the jdm "cur_path" push (see RobotStateTracker.CleanPath); takes over from the REST snapshot in _lastPath as soon as it has any points.</summary>
    private IReadOnlyList<MyDyson.Core.Point>? _liveTrail;
    private IReadOnlyList<MyDyson.Core.Point>? _liveObstacles;
    private IReadOnlyList<DirtSpot>? _liveDirt;

    // ---- Robot state ----------------------------------------------------------

    public void Apply(RobotState s)
    {
        _robotReady = s.IsDocked || s.State is "INACTIVE_DISCHARGING" or "FULL_CLEAN_FINISHED" or "ABORTED";
        UpdateCanStart();
        _robotPosition = s.LatestPosition ?? _robotPosition;
        RebuildScene();
    }

    /// <summary>
    /// The start button needs both halves: a robot that can take a job, and at least one room
    /// ticked. Clicking it with nothing selected used to be answered by a red message, which is a
    /// worse way to say "not yet" than a button that plainly cannot be pressed.
    /// </summary>
    private void UpdateCanStart()
    {
        CanStart = _robotReady && !HasSpot && Zones.Any(z => z.Selected);
        CanStartSpot = _robotReady && SpotCorners is not null && SelectedMap is not null;
    }

    public void SetLiveTrail(IReadOnlyList<RobotPosition> path)
    {
        _liveTrail = path.Select(p => new MyDyson.Core.Point(p.X, p.Y, p.Update)).ToList();
        RebuildScene();
    }

    // ---- Maps and zones -------------------------------------------------------

    /// <summary>
    /// Reloads the map list. The map the user was looking at is kept when it is still there, so a
    /// refresh — asked for, or automatic after a reconnection — does not yank them back to the
    /// active map; the rooms they had ticked survive it too (see <see cref="LoadZonesAsync"/>).
    /// </summary>
    public async Task LoadMapsAsync()
    {
        try
        {
            var wanted = SelectedMap?.Id;
            await maps.LoadAsync();
            SelectedMap = (wanted is null ? null : maps.Find(wanted))
                ?? Maps.FirstOrDefault(m => m.Metadata.IsCurrentMap)
                ?? Maps.FirstOrDefault();
        }
        catch (OperationCanceledException) when (hub.IsShuttingDown) { }
        catch (Exception ex) { hub.AddLog($"cartes: {ex.Message}"); }
    }

    partial void OnSelectedMapChanged(MapItem? value)
    {
        // A zone drawn on one map means nothing on another.
        DrawingSpot = false;
        SpotCorners = null;
        if (value is null) return;
        _ = LoadZonesAsync(value);
    }

    private async Task LoadZonesAsync(MapItem map)
    {
        // A reload rebuilds every row from fresh metadata, so what the user had ticked has to be
        // carried over by id: otherwise a refresh — or a wifi blip, now that a reconnection
        // reloads — would silently clear a selection they were about to start a clean with.
        var wasSelected = Zones.Where(z => z.Selected).ToDictionary(z => z.Id, z => z.Order, StringComparer.Ordinal);
        Zones.Clear();
        _nextOrder = 1;
        // The cloud API returns zones in an unexplained order (probably the order the robot
        // detected them in while mapping), neither by id nor alphabetical. Sorting by name
        // gives a predictable list; it will not exactly match the phone app, which appears to
        // group by room type rather than sort by name.
        var ordered = (map.Metadata.Zones ?? []).OrderBy(z => z.Name, StringComparer.Create(new CultureInfo("fr-FR"), ignoreCase: true));
        foreach (var z in ordered)
        {
            var item = new ZoneItem(z);
            // Restored before subscribing, so restoring is not mistaken for a click: the handler
            // below would renumber the room instead of putting it back where it was.
            if (wasSelected.TryGetValue(item.Id, out var order))
            {
                item.Selected = true;
                item.Order = order;
                _nextOrder = Math.Max(_nextOrder, order + 1);
            }
            // Subscribed after construction, so only the user's edits land here — including any
            // made while the geometry below is still downloading, which must not be lost.
            item.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ZoneItem.Selected)) { OnZoneSelectionChanged(item); UpdateCanStart(); }
                else if (e.PropertyName is nameof(ZoneItem.SelectedCleanType) or nameof(ZoneItem.SelectedStrategy)
                         or nameof(ZoneItem.SelectedWaterLevel) or nameof(ZoneItem.SelectedMopPasses))
                    ScheduleZoneSettingsPersist();
                RebuildScene();
            };
            Zones.Add(item);
        }
        UpdateCanStart();   // the restored selection counts too
        await LoadMapGeometryAsync(map.Id);
        RebuildScene();
    }

    private void OnZoneSelectionChanged(ZoneItem item)
    {
        if (item.Selected) item.Order = _nextOrder++;
        else
        {
            // Close the gap so the badges stay 1, 2, 3…
            var removed = item.Order;
            item.Order = 0;
            foreach (var z in Zones.Where(z => z.Selected && z.Order > removed)) z.Order--;
            _nextOrder = Math.Max(1, _nextOrder - 1);
        }
    }

    /// <summary>Called by the map view when a room is clicked. Ignored while a zone is drawn: the zone replaces the rooms.</summary>
    public void ToggleZone(string zoneId)
    {
        if (HasSpot) return;
        var z = Zones.FirstOrDefault(z => z.Id == zoneId);
        if (z is not null) z.Selected = !z.Selected;
    }

    /// <summary>
    /// Called by the map view when the user clicks/taps empty map space. With a zone drawn, it
    /// erases the zone instead — unless the click landed on the zone itself, which may well stick
    /// out past the walls into empty space.
    /// </summary>
    public void ClearSelection()
    {
        if (HasSpot)
        {
            if (!_lastClickInSpot) SpotCorners = null;
            return;
        }
        foreach (var z in Zones) z.Selected = false;
    }

    /// <summary>Whether the last click on the map fell inside the drawn zone; see <see cref="MapClickedAt"/>.</summary>
    private bool _lastClickInSpot;

    /// <summary>Called by the map view for every click, before <see cref="ToggleZone"/> or <see cref="ClearSelection"/>.</summary>
    public void MapClickedAt(MyDyson.Core.Point at) =>
        _lastClickInSpot = SpotCorners is { } corners && MapShapes.Contains(corners, at.X, at.Y);

    private async Task LoadMapGeometryAsync(string mapId)
    {
        try
        {
            _map = await maps.GetMapAsync(mapId);
            // The occupancy grid and the live robot position/path only ever describe the active map.
            if (maps.IsCurrent(mapId) && await maps.LoadGridAsync(mapId))
            {
                var live = await hub.Api.GetLiveCleaningMapAsync(hub.Serial, hub.Ct);
                _robotPosition ??= live.RobotLocation;
                _lastPath = live.CleanPath;
                _liveObstacles = live.Obstacles;
                _liveDirt = live.Dirt;
            }
            RebuildScene();
        }
        catch (OperationCanceledException) when (hub.IsShuttingDown) { }
        catch (Exception ex) { hub.AddLog($"carte {mapId}: {ex.Message}"); }
    }

    public void RebuildScene()
    {
        var mapId = SelectedMap?.Id;
        var isCurrent = SelectedMap?.Metadata.IsCurrentMap == true;
        Scene = new MapScene
        {
            Grid = isCurrent ? maps.GridFor(mapId) : null,
            Map = _map?.Id == mapId ? _map : null,
            ZoneMetadata = SelectedMap?.Metadata.Zones,
            Dock = _map?.Id == mapId ? _map?.DockLocation : null,
            Robot = isCurrent ? _robotPosition : null,
            Path = isCurrent ? (_liveTrail is { Count: > 0 } ? _liveTrail : _lastPath) : null,
            Obstacles = isCurrent ? _liveObstacles : null,
            DirtSpots = isCurrent ? _liveDirt : null,
            SelectedZoneIds = Zones.Where(z => z.Selected).Select(z => z.Id).ToHashSet(),
            ZoneOrder = Zones.Where(z => z.Selected).ToDictionary(z => z.Id, z => z.Order),
            ShowFurniture = display.ShowFurniture,
            ShowTravelPath = display.ShowTravelPath,
            SpotZone = SpotCorners,
        };
    }

    // ---- Room settings persistence -------------------------------------------

    private static readonly TimeSpan PersistZonesDebounce = TimeSpan.FromMilliseconds(500);
    private CancellationTokenSource? _persistZonesPending;
    private readonly SemaphoreSlim _persistZonesLock = new(1, 1);

    /// <summary>
    /// Saves the per-room settings on the cloud so the phone app shows the same choice. Every
    /// ComboBox change lands here and the PUT carries the whole zone list, so changes are coalesced
    /// for half a second and sent one at a time; the rooms' state at send time is what goes out.
    /// The map and its rooms are pinned now, so switching maps during the wait cannot redirect
    /// the save to the wrong map.
    /// </summary>
    private void ScheduleZoneSettingsPersist()
    {
        if (SelectedMap is not { } map) return;
        _persistZonesPending?.Cancel();
        _persistZonesPending?.Dispose();
        var pending = _persistZonesPending = CancellationTokenSource.CreateLinkedTokenSource(hub.Ct);
        _ = PersistZoneSettingsAsync(map, Zones.ToList(), pending.Token);
    }

    private async Task PersistZoneSettingsAsync(MapItem map, IReadOnlyList<ZoneItem> zones, CancellationToken pending)
    {
        try
        {
            await Task.Delay(PersistZonesDebounce, pending);
            await _persistZonesLock.WaitAsync(pending);
            try
            {
                // Once it is on the wire a newer edit no longer cancels it: that edit queues its own
                // PUT behind this one, and only shutting down aborts the request.
                await hub.Api.UpdateMapZonesAsync(hub.Serial, map.Id, zones.Select(z => z.ToMetadata()).ToList(), hub.Ct);
                hub.AddLog("réglages des pièces enregistrés");
            }
            finally { _persistZonesLock.Release(); }
        }
        catch (OperationCanceledException) when (pending.IsCancellationRequested) { } // superseded, or shutting down
        catch (Exception ex) { hub.AddLog($"réglages des pièces: {ex.Message}"); }
    }

    // ---- Cleaning a zone drawn on the map --------------------------------------

    /// <summary>A zone narrower than this is a slip of the mouse rather than something to clean.</summary>
    private const double MinimumSpotSide = 0.3;

    /// <summary>True while the user is drawing the zone's rectangle on the map; the view turns clicks into corners.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(DrawSpotLabel))] private bool _drawingSpot;
    /// <summary>The drawn zone, in the order the phone sends it; null until one is drawn.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasSpot)), NotifyPropertyChangedFor(nameof(SpotSizeText))]
    private IReadOnlyList<MyDyson.Core.Point>? _spotCorners;
    [ObservableProperty] private bool _canStartSpot;

    public bool HasSpot => SpotCorners is not null;
    public string DrawSpotLabel => DrawingSpot ? "Annuler le tracé" : HasSpot ? "Tracer une autre zone…" : "Tracer une zone…";
    public string SpotSizeText => SpotCorners is { } c
        ? string.Create(CultureInfo.CurrentCulture, $"Zone de {MapShapes.Sides(c).First:0.0} × {MapShapes.Sides(c).Second:0.0} m")
        : "";

    // The same four choices as a room, from the same lists.
    public IReadOnlyList<CleanTypeOption> SpotCleanTypes { get; } = ZoneItem.CleanTypeOptions;
    public IReadOnlyList<StrategyOption> SpotStrategies { get; } = ZoneItem.StrategyOptions;
    public IReadOnlyList<WaterLevelOption> SpotWaterLevels { get; } = ZoneItem.WaterLevelOptions;
    public IReadOnlyList<MopPassesOption> SpotMopPasses { get; } = ZoneItem.MopPassesOptions;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(SpotHasVacuum)), NotifyPropertyChangedFor(nameof(SpotHasMop))]
    private CleanTypeOption _spotCleanType = ZoneItem.CleanTypeOptions[0];
    [ObservableProperty] private StrategyOption _spotStrategy = ZoneItem.StrategyOptions[0];
    [ObservableProperty] private WaterLevelOption _spotWaterLevel = ZoneItem.WaterLevelOptions[0];
    [ObservableProperty] private MopPassesOption _spotMopPass = ZoneItem.MopPassesOptions[0];
    public bool SpotHasVacuum => SpotCleanType.Value is not CleanType.Mop;
    public bool SpotHasMop => SpotCleanType.Value is not CleanType.Vacuum;

    partial void OnSpotCornersChanged(IReadOnlyList<MyDyson.Core.Point>? value)
    {
        // A zone replaces the rooms: they are unticked, and stay out of reach until it is erased.
        if (value is not null) foreach (var z in Zones) z.Selected = false;
        OnPropertyChanged(nameof(DrawSpotLabel));
        OnPropertyChanged(nameof(RoomsEnabled));
        UpdateCanStart();
        RebuildScene();
    }

    /// <summary>Whether rooms can be ticked: not while a zone is drawn, which is cleaned instead.</summary>
    public bool RoomsEnabled => !HasSpot;

    /// <summary>First press: draw a zone by clicking two opposite corners on the map. Pressed again while drawing: give up.</summary>
    [RelayCommand]
    private void DrawSpot() => DrawingSpot = !DrawingSpot && SelectedMap is not null;

    [RelayCommand]
    private void ClearSpot()
    {
        DrawingSpot = false;
        SpotCorners = null;
    }

    /// <summary>Called by the window once both corners have been clicked on the map.</summary>
    public void SpotDrawn(MyDyson.Core.Point a, MyDyson.Core.Point b)
    {
        DrawingSpot = false;
        if (Math.Abs(a.X - b.X) < MinimumSpotSide || Math.Abs(a.Y - b.Y) < MinimumSpotSide)
        {
            hub.Message = "Zone trop étroite : il faut au moins 30 cm de côté.";
            return;
        }
        hub.Message = "";
        SpotCorners = SpotCleanSequence.Corners(a, b);
    }

    /// <summary>
    /// Called by the window when the zone has been dragged or stretched on the map. The corners are
    /// put back in the phone's order; a zone squeezed below the minimum keeps its former size.
    /// </summary>
    public void SpotEdited(IReadOnlyList<MyDyson.Core.Point> corners)
    {
        if (corners.Count != 4 || SpotCorners is null) return;
        var (a, b) = (corners[0], corners[2]);
        if (Math.Abs(a.X - b.X) < MinimumSpotSide || Math.Abs(a.Y - b.Y) < MinimumSpotSide)
        {
            hub.Message = "Zone trop étroite : il faut au moins 30 cm de côté.";
            SpotCorners = [.. SpotCorners];   // a new list, so the map drops its preview and shows the zone as it was
            return;
        }
        hub.Message = "";
        SpotCorners = SpotCleanSequence.Corners(a, b);
    }

    [RelayCommand]
    private Task StartSpotCleanAsync()
    {
        if (SpotCorners is not { } corners || SelectedMap is null) return Task.CompletedTask;
        if (!long.TryParse(SelectedMap.Id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var mapId))
        {
            hub.Message = $"Identifiant de carte inattendu : {SelectedMap.Id}";
            return Task.CompletedTask;
        }
        var settings = new RoomSettings(SpotCleanType.Value, SpotStrategy.Value, SpotWaterLevel.Value, SpotMopPass.Value);
        return hub.RunAsync($"nettoyage de la zone ({SpotSizeText.ToLower(CultureInfo.CurrentCulture)})",
            c => SpotCleanSequence.StartAsync(c, mapId, corners, settings, ct: hub.Ct));
    }

    // ---- Commands ------------------------------------------------------------

    [RelayCommand]
    private Task StartCleanAsync()
    {
        var rooms = Zones.Where(z => z.Selected).OrderBy(z => z.Order)
            .Select(z => new RoomSelection(z.Id, z.Settings, z.Order)).ToList();
        if (rooms.Count == 0 || SelectedMap is null) { hub.Message = "Sélectionnez au moins une pièce."; return Task.CompletedTask; }
        // Outside RunAsync's try, so a non-numeric id must not throw: that would surface as an
        // unhandled exception in the command dispatch rather than a message.
        if (!long.TryParse(SelectedMap.Id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var mapId))
        {
            hub.Message = $"Identifiant de carte inattendu : {SelectedMap.Id}";
            return Task.CompletedTask;
        }
        return hub.RunAsync($"démarrage de {rooms.Count} pièce(s)", c => CleaningSequence.StartAsync(c, mapId, rooms));
    }

    /// <summary>Makes the selected map the account's active map, as the phone app does from its map picker.</summary>
    [RelayCommand]
    private async Task SetActiveMapAsync()
    {
        if (SelectedMap is not { } map || map.Metadata.IsCurrentMap) return;
        await hub.RunAsync($"carte active : {map.Metadata.Name}", c => c.SetCurrentMapAsync(long.Parse(map.Id, CultureInfo.InvariantCulture)));
        await LoadMapsAsync();
    }

    [RelayCommand]
    private void ExportMap()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog { Filter = "Image PNG|*.png", FileName = $"carte-{SelectedMap?.Metadata.Name ?? "robot"}.png" };
        if (dlg.ShowDialog() == true)
        {
            MapRenderer.ExportPng(Scene, 1200, 1400, dlg.FileName);
            hub.AddLog($"carte exportée vers {dlg.FileName}");
        }
    }

    /// <summary>
    /// The persist lock is left alone on purpose: a PUT cancelled by the lifetime token still
    /// releases it from a later continuation, and an unused SemaphoreSlim holds nothing that needs
    /// disposing.
    /// </summary>
    public void Dispose() => _persistZonesPending?.Dispose();
}
