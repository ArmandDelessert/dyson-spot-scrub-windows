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
/// The Nettoyage card and the Carte tab: which map, which rooms in which order with which
/// settings, and the live scene (grid, robot, trail, obstacles, stains) drawn from it.
/// </summary>
public sealed partial class CleaningViewModel : ObservableObject, IDisposable
{
    private readonly RobotHub _hub;
    private readonly MapCatalog _maps;
    private readonly AppSettings _settings;

    public CleaningViewModel(RobotHub hub, MapCatalog maps, AppSettings settings)
    {
        _hub = hub;
        _maps = maps;
        _settings = settings;
        // A click on a room ticks it; on empty space it clears the ticks, or the drawn zone. While
        // a zone is being drawn, clicks pick its corners; once drawn, it can be dragged or
        // stretched from its corners like a restriction zone.
        Canvas = new MapInteraction { EditableShapeResizable = true, MinimumShapeSide = MinimumSpotSide, ShowPointerCoordinates = settings.ShowPointerCoordinates, ShowShapeDimensions = settings.ShowShapeDimensions };
        Canvas.WorldClicked += MapClickedAt;
        Canvas.ZoneClicked += ToggleZone;
        Canvas.EmptySpaceClicked += ClearSelection;
        Canvas.RectanglePicked += SpotDrawn;
        Canvas.ShapeEdited += SpotEdited;
    }

    /// <summary>The Carte tab's map: zoom, pan, and what clicks on it mean.</summary>
    public MapInteraction Canvas { get; }

    public ObservableCollection<MapItem> Maps => _maps.Maps;
    /// <summary>Exposed so the map toolbar can bind straight to it: a Popup sits outside the window's visual tree, where an ancestor lookup would find nothing.</summary>
    public AppSettings AppSettings => _settings;
    [ObservableProperty] private MapItem? _selectedMap;
    public ObservableCollection<ZoneItem> Zones { get; } = new();
    [ObservableProperty] private MapScene _scene = new();
    [ObservableProperty] private bool _canStart;

    partial void OnSceneChanged(MapScene value) => Canvas.Scene = value;

    private PersistentMap? _map;
    private bool _robotReady;
    private int _nextOrder = 1;
    private RobotPosition? _robotPosition;
    private IReadOnlyList<DyssCockpit.Core.Point>? _lastPath;
    /// <summary>Grown in real time from the jdm "cur_path" push (see RobotStateTracker.CleanPath); takes over from the REST snapshot in _lastPath as soon as it has any points.</summary>
    private IReadOnlyList<DyssCockpit.Core.Point>? _liveTrail;
    private IReadOnlyList<DyssCockpit.Core.Point>? _liveObstacles;
    private IReadOnlyList<DirtSpot>? _liveDirt;
    /// <summary>The rectangles of the current or last task, from the cloud's live map: a zone clean started from the phone, a schedule, or before this window opened.</summary>
    private IReadOnlyList<IReadOnlyList<DyssCockpit.Core.Point>>? _liveSpots;

    /// <summary>How long after a start the cloud's live map is first read for the new task: its rooms are not marked at once.</summary>
    public TimeSpan LiveZonesDelay { get; init; } = TimeSpan.FromSeconds(5);

    // ---- Robot state ----------------------------------------------------------

    public void Apply(RobotState s)
    {
        _robotReady = s.IsDocked || s.State is "INACTIVE_DISCHARGING" or "FULL_CLEAN_FINISHED" or "ABORTED";
        UpdateCanStart();
        _robotPosition = s.LatestPosition ?? _robotPosition;
        _activity.Apply(s);
        if (s.State is not null)
        {
            var atWork = s.IsCleaning || s.IsMapping;
            if (atWork && _idleSeen) ForgetLastTask();
            _idleSeen = !atWork;
        }
        FollowTask(s);
        RebuildScene();
    }

    /// <summary>
    /// Whether the robot was last seen idle (or done), so that its next clean or mapping is a new task.
    /// False until a state says so: opened in the middle of a clean, the trail loaded is that clean's.
    /// </summary>
    private bool _idleSeen;

    /// <summary>
    /// A new task has begun: the last one's trail, obstacles, stains and zones are in the history now,
    /// and are not shown in the moments before the new task's own arrive. Its zones and rooms are
    /// read from the cloud by <see cref="LiveLoopAsync"/>: a clean started from the phone or a
    /// schedule says nothing of them.
    /// </summary>
    private void ForgetLastTask()
    {
        _lastPath = null;
        _liveTrail = null;
        _liveObstacles = null;
        _liveDirt = null;
        _liveSpots = null;
        _liveRooms = null;
    }

    /// <summary>What the robot and its dock are doing, for their icons on the map.</summary>
    private readonly RobotActivityTracker _activity = new();

    public void ApplyJdm(JdmProperties jdm)
    {
        var before = (_activity.Docked, _activity.Robot, _activity.Dock, _activity.TaskOver);
        _activity.ApplyJdm(jdm);
        if ((_activity.Docked, _activity.Robot, _activity.Dock, _activity.TaskOver) != before) RebuildScene();
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
        _liveTrail = path.Select(p => new DyssCockpit.Core.Point(p.X, p.Y, p.Update)).ToList();
        // Emptied at the start of a task: the trail read from the cloud is the last task's, not a stand-in.
        if (path.Count == 0) _lastPath = null;
        _activity.ApplyTrail(path);
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
            await _maps.LoadAsync();
            SelectedMap = (wanted is null ? null : _maps.Find(wanted))
                ?? Maps.FirstOrDefault(m => m.Metadata.IsCurrentMap)
                ?? Maps.FirstOrDefault();
        }
        catch (OperationCanceledException) when (_hub.IsShuttingDown) { }
        catch (Exception ex) { _hub.AddLog(Tf("Cleaning_Maps", ex.Message)); }
    }

    partial void OnSelectedMapChanged(MapItem? value)
    {
        // Zones drawn on one map mean nothing on another.
        DrawingSpot = false;
        ClearSpots();
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
            // Ticking a room does not unfold its four lists: they are set for all the ticked rooms at once.
            var item = new ZoneItem(z) { ExpandOnSelect = false };
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
                if (e.PropertyName == nameof(ZoneItem.Selected)) { OnZoneSelectionChanged(item); UpdateCanStart(); RefreshCommon(); }
                else if (e.PropertyName is nameof(ZoneItem.SelectedCleanType) or nameof(ZoneItem.SelectedStrategy)
                         or nameof(ZoneItem.SelectedWaterLevel) or nameof(ZoneItem.SelectedMopPasses))
                {
                    ScheduleZoneSettingsPersist();
                    RefreshCommon();
                }
                RebuildScene();
            };
            Zones.Add(item);
        }
        UpdateCanStart();   // the restored selection counts too
        RefreshCommon();
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

    /// <summary>Called by the map when a room is clicked. Ignored while a zone is drawn: the zone replaces the rooms.</summary>
    public void ToggleZone(string zoneId)
    {
        if (HasSpot) return;
        var z = Zones.FirstOrDefault(z => z.Id == zoneId);
        if (z is not null) z.Selected = !z.Selected;
    }

    /// <summary>
    /// Called by the map when the user clicks/taps empty map space. With zones drawn, it erases
    /// them instead, as it unticks every room — unless the click landed on a zone, which may well
    /// stick out past the walls into empty space.
    /// </summary>
    public void ClearSelection()
    {
        if (HasSpot)
        {
            if (!_lastClickInSpot) ClearSpots();
            return;
        }
        foreach (var z in Zones) z.Selected = false;
    }

    /// <summary>Whether the last click on the map fell inside a drawn zone; see <see cref="MapClickedAt"/>.</summary>
    private bool _lastClickInSpot;

    /// <summary>
    /// Called by the map for every click, before <see cref="ToggleZone"/> or <see cref="ClearSelection"/>.
    /// A click on a zone other than the one being edited makes it the one being edited.
    /// </summary>
    public void MapClickedAt(DyssCockpit.Core.Point at)
    {
        var hit = SpotZones.LastOrDefault(z => MapShapes.Contains(z, at.X, at.Y));
        _lastClickInSpot = hit is not null;
        if (hit is not null && !ReferenceEquals(hit, SpotCorners)) SpotCorners = hit;
    }

    private async Task LoadMapGeometryAsync(string mapId)
    {
        try
        {
            _map = await _maps.GetMapAsync(mapId);
            // The occupancy grid and the live robot position/path only ever describe the active map.
            if (_maps.IsCurrent(mapId) && await _maps.LoadGridAsync(mapId))
            {
                var live = await _hub.Api.GetLiveCleaningMapAsync(_hub.Serial, _hub.Ct);
                _robotPosition ??= live.RobotLocation;
                _lastPath = live.CleanPath;
                _liveObstacles = live.Obstacles;
                _liveDirt = live.Dirt;
                _liveSpots = SpotZone.Rectangles(live.SpotZones);
                _liveRooms = live.Zones;
            }
            RebuildScene();
        }
        catch (OperationCanceledException) when (_hub.IsShuttingDown) { }
        catch (Exception ex) { _hub.AddLog(Tf("Cleaning_Map", mapId, ex.Message)); }
    }

    public void RebuildScene()
    {
        // The preference may just have changed: the scene is rebuilt on every change of preferences.
        Canvas.ShowPointerCoordinates = _settings.ShowPointerCoordinates;
        Canvas.ShowShapeDimensions = _settings.ShowShapeDimensions;
        var mapId = SelectedMap?.Id;
        var isCurrent = SelectedMap?.Metadata.IsCurrentMap == true;
        RebuildTask();
        // While a task is under way the map shows its rooms, whatever the cloud still has ticked from the last choice.
        var taskRooms = ShowTask && TaskRooms.Count > 0 ? TaskRooms.Select(r => r.Id).ToHashSet() : null;
        Scene = new MapScene
        {
            Grid = isCurrent ? _maps.GridFor(mapId) : null,
            Map = _map?.Id == mapId ? _map : null,
            ZoneMetadata = SelectedMap?.Metadata.Zones,
            Dock = _map?.Id == mapId ? _map?.DockLocation : null,
            Robot = isCurrent ? _robotPosition : null,
            // The last task's trail, obstacles and stains stay on the map until the robot is done
            // with it, as on the phone; after that they are in the history.
            Path = isCurrent && !_activity.TaskOver ? (_liveTrail is { Count: > 0 } ? _liveTrail : _lastPath) : null,
            Obstacles = isCurrent && !_activity.TaskOver ? _liveObstacles : null,
            DirtSpots = isCurrent && !_activity.TaskOver ? _liveDirt : null,
            SelectedZoneIds = taskRooms ?? Zones.Where(z => z.Selected).Select(z => z.Id).ToHashSet(),
            ZoneOrder = taskRooms is not null ? null : Zones.Where(z => z.Selected).ToDictionary(z => z.Id, z => z.Order),
            ShowFurniture = _settings.ShowFurniture,
            ShowTravelPath = _settings.ShowTravelPath,
            ShowCleanedArea = _settings.ShowCleanedArea,
            // The zones drawn here; with none, those of the zone clean under way, there as long as its trail.
            SpotZones = SpotZones.Count > 0 ? SpotZones
                : isCurrent && !_activity.TaskOver && _liveSpots is { Count: > 0 } ? _liveSpots
                : null,
            RobotActivity = isCurrent ? _activity.Robot : RobotActivity.Idle,
            DockActivity = isCurrent ? _activity.Dock : DockActivity.Idle,
            RobotDocked = isCurrent && _activity.Docked,
            SmoothRobotMotion = _settings.SmoothRobotMotion,
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
        var pending = _persistZonesPending = CancellationTokenSource.CreateLinkedTokenSource(_hub.Ct);
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
                await _hub.Api.UpdateMapZonesAsync(_hub.Serial, map.Id, zones.Select(z => z.ToMetadata()).ToList(), _hub.Ct);
                _hub.AddLog(T("Cleaning_RoomSettingsSaved"));
            }
            finally { _persistZonesLock.Release(); }
        }
        catch (OperationCanceledException) when (pending.IsCancellationRequested) { } // superseded, or shutting down
        catch (Exception ex) { _hub.AddLog(Tf("Cleaning_RoomSettings", ex.Message)); }
    }

    // ---- Cleaning a zone drawn on the map --------------------------------------

    /// <summary>A zone narrower than this is a slip of the mouse rather than something to clean.</summary>
    public const double MinimumSpotSide = 0.3;

    /// <summary>True while the user is drawing a zone's rectangle on the map; the view turns clicks into corners.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(DrawSpotLabel))] private bool _drawingSpot;

    /// <summary>
    /// Every zone drawn, in the order drawn, each in the order the phone sends a zone's corners:
    /// they are cleaned one after the other in one clean. Empty until one is drawn.
    /// </summary>
    public IReadOnlyList<IReadOnlyList<DyssCockpit.Core.Point>> SpotZones { get; private set; } = [];

    /// <summary>The zone the map lets move and stretch, one of <see cref="SpotZones"/>: the last drawn, or the last clicked; null when there is none.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasSpot)), NotifyPropertyChangedFor(nameof(SpotSizeText)), NotifyPropertyChangedFor(nameof(ClearSpotLabel))]
    private IReadOnlyList<DyssCockpit.Core.Point>? _spotCorners;
    [ObservableProperty] private bool _canStartSpot;

    public bool HasSpot => SpotCorners is not null;
    public string DrawSpotLabel => DrawingSpot ? T("Cleaning_CancelDrawing") : HasSpot ? T("Cleaning_DrawAnotherZone") : T("Cleaning_DrawAZone");
    public string ClearSpotLabel => SpotZones.Count > 1 ? T("Cleaning_EraseTheZones") : T("Cleaning_EraseTheZone");
    public string SpotSizeText => HasSpot ? MapGeometry.ZonesText(SpotZones) : "";

    /// <summary>The zones become <paramref name="zones"/>, and the one being edited <paramref name="active"/>.</summary>
    private void SetSpots(IReadOnlyList<IReadOnlyList<DyssCockpit.Core.Point>> zones, IReadOnlyList<DyssCockpit.Core.Point>? active)
    {
        SpotZones = zones;
        if (ReferenceEquals(SpotCorners, active)) RebuildScene();
        else SpotCorners = active;
        OnPropertyChanged(nameof(SpotSizeText));
        OnPropertyChanged(nameof(ClearSpotLabel));
    }

    /// <summary>The zone being edited, replaced by <paramref name="zone"/>, which takes its place in the order.</summary>
    private void ReplaceActiveSpot(IReadOnlyList<DyssCockpit.Core.Point> zone) =>
        SetSpots([.. SpotZones.Select(z => ReferenceEquals(z, SpotCorners) ? zone : z)], zone);

    private void ClearSpots() => SetSpots([], null);

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

    partial void OnSpotCornersChanged(IReadOnlyList<DyssCockpit.Core.Point>? value)
    {
        // A zone replaces the rooms: they are unticked, and stay out of reach until it is erased.
        if (value is not null) foreach (var z in Zones) z.Selected = false;
        OnPropertyChanged(nameof(DrawSpotLabel));
        OnPropertyChanged(nameof(RoomsEnabled));
        UpdateCanStart();
        RebuildScene();
        Canvas.EditableShape = value;
    }

    /// <summary>While drawing, clicks on the map pick the zone's two opposite corners.</summary>
    partial void OnDrawingSpotChanged(bool value) => Canvas.Picking = value ? MapPick.Rectangle : MapPick.None;

    /// <summary>Whether rooms can be ticked: not while a zone is drawn, which is cleaned instead.</summary>
    public bool RoomsEnabled => !HasSpot;

    /// <summary>First press: draw a zone by clicking two opposite corners on the map. Pressed again while drawing: give up.</summary>
    [RelayCommand]
    private void DrawSpot() => DrawingSpot = !DrawingSpot && SelectedMap is not null;

    /// <summary>Erases every zone drawn.</summary>
    [RelayCommand]
    private void ClearSpot()
    {
        DrawingSpot = false;
        ClearSpots();
    }

    /// <summary>
    /// Escape: gives up the drawing under way, or else erases the zone being edited, the one drawn
    /// before it becoming the one edited. False when there was neither, so the key is left to
    /// whatever else it may mean.
    /// </summary>
    public bool CancelSpot()
    {
        if (DrawingSpot) DrawingSpot = false;
        else if (SpotCorners is { } active)
        {
            var rest = SpotZones.Where(z => !ReferenceEquals(z, active)).ToList();
            SetSpots(rest, rest.LastOrDefault());
        }
        else return false;
        return true;
    }

    /// <summary>Called by the map once both corners have been clicked.</summary>
    public void SpotDrawn(DyssCockpit.Core.Point a, DyssCockpit.Core.Point b)
    {
        DrawingSpot = false;
        if (Math.Abs(a.X - b.X) < MinimumSpotSide - MapShapes.Tolerance || Math.Abs(a.Y - b.Y) < MinimumSpotSide - MapShapes.Tolerance)
        {
            _hub.Message = T("Cleaning_ZoneTooNarrowEachSide");
            return;
        }
        _hub.Message = "";
        IReadOnlyList<DyssCockpit.Core.Point> zone = SpotCleanSequence.Corners(a, b);
        SetSpots([.. SpotZones, zone], zone);
    }

    /// <summary>
    /// Called by the map when the zone has been dragged or stretched on it. The corners are
    /// put back in the phone's order; a zone squeezed below the minimum keeps its former size.
    /// </summary>
    public void SpotEdited(IReadOnlyList<DyssCockpit.Core.Point> corners)
    {
        if (corners.Count != 4 || SpotCorners is null) return;
        var (a, b) = (corners[0], corners[2]);
        if (Math.Abs(a.X - b.X) < MinimumSpotSide - MapShapes.Tolerance || Math.Abs(a.Y - b.Y) < MinimumSpotSide - MapShapes.Tolerance)
        {
            _hub.Message = T("Cleaning_ZoneTooNarrowEachSide");
            ReplaceActiveSpot([.. SpotCorners]);   // a new list, so the map drops its preview and shows the zone as it was
            return;
        }
        _hub.Message = "";
        ReplaceActiveSpot(SpotCleanSequence.Corners(a, b));
    }

    [RelayCommand]
    private Task StartSpotCleanAsync()
    {
        if (SpotZones is not { Count: > 0 } zones || SelectedMap is null) return Task.CompletedTask;
        if (!long.TryParse(SelectedMap.Id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var mapId))
        {
            _hub.Message = Tf("Cleaning_UnexpectedMapId", SelectedMap.Id);
            return Task.CompletedTask;
        }
        var settings = new RoomSettings(SpotCleanType.Value, SpotStrategy.Value, SpotWaterLevel.Value, SpotMopPass.Value);
        LastLaunch = new LaunchedClean([], _hub.Time.GetUtcNow());
        var spots = zones.Select(z => (Guid.NewGuid().ToString(), z)).ToList();
        return _hub.RunAsync(Tf("Cleaning_Cleaning", SpotSizeText),
            c => SpotCleanSequence.StartAsync(c, mapId, spots, settings, _hub.Ct));
    }

    // ---- Commands ------------------------------------------------------------

    [RelayCommand]
    private Task StartCleanAsync()
    {
        var rooms = Zones.Where(z => z.Selected).OrderBy(z => z.Order)
            .Select(z => new RoomSelection(z.Id, z.Settings, z.Order)).ToList();
        if (rooms.Count == 0 || SelectedMap is null) { _hub.Message = T("Cleaning_SelectAtLeastOneRoom"); return Task.CompletedTask; }
        // Outside RunAsync's try, so a non-numeric id must not throw: that would surface as an
        // unhandled exception in the command dispatch rather than a message.
        if (!long.TryParse(SelectedMap.Id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var mapId))
        {
            _hub.Message = Tf("Cleaning_UnexpectedMapId", SelectedMap.Id);
            return Task.CompletedTask;
        }
        LastLaunch = new LaunchedClean(Zones.Where(z => z.Selected).OrderBy(z => z.Order).Select(z => z.Name).ToList(), _hub.Time.GetUtcNow());
        var mapRooms = Zones.Where(z => int.TryParse(z.Id, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            .Select(z => new MapRoom(int.Parse(z.Id, CultureInfo.InvariantCulture), z.Name)).ToList();
        return _hub.RunAsync(Tf("Cleaning_StartingRoomS", rooms.Count), c => CleaningSequence.StartAsync(c, mapId, rooms, mapRooms));
    }

    /// <summary>What this window last sent the robot to clean — the rooms, or none for a drawn zone — and when. The robot's own messages never say which rooms it set off for.</summary>
    public LaunchedClean? LastLaunch { get; internal set; }

    public sealed record LaunchedClean(IReadOnlyList<string> Rooms, DateTimeOffset At);

    /// <summary>
    /// The name the PNG export of a map is offered under: <c>Carte-Salon-2026-10-11-14-05-09.png</c> (<c>Map-</c> in English), the
    /// map's name then the moment of the export, so that two exports never collide and sort by date. Characters
    /// Windows does not allow in a file name are replaced, a name that was all of them leaves the map unnamed.
    /// </summary>
    internal static string ExportFileName(string? mapName, DateTimeOffset now)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var name = new string([.. (mapName ?? "").Select(c => invalid.Contains(c) ? '_' : c)]).Trim().TrimEnd('.');
        if (name.Length == 0) name = "Robot";
        return string.Create(CultureInfo.InvariantCulture, $"{T("Cleaning_MapFile")}-{name}-{now:yyyy-MM-dd-HH-mm-ss}.png");
    }

    /// <summary>Makes the selected map the account's active map, as the phone app does from its map picker.</summary>
    [RelayCommand]
    private async Task SetActiveMapAsync()
    {
        if (SelectedMap is not { } map || map.Metadata.IsCurrentMap) return;
        await _hub.RunAsync(Tf("Cleaning_ActiveMap", map.Metadata.Name), c => c.SetCurrentMapAsync(long.Parse(map.Id, CultureInfo.InvariantCulture)));
        await LoadMapsAsync();
    }

    [RelayCommand]
    private async Task ExportMapAsync()
    {
        if (await _hub.Dialogs.SaveMapImageAsync(Scene, ExportFileName(SelectedMap?.Metadata.Name, _hub.Time.GetLocalNow())) is { } path)
            _hub.AddLog(Tf("Cleaning_MapExportedTo", path));
    }

    /// <summary>
    /// The persist lock is left alone on purpose: a PUT cancelled by the lifetime token still
    /// releases it from a later continuation, and an unused SemaphoreSlim holds nothing that needs
    /// disposing.
    /// </summary>
    public void Dispose() => _persistZonesPending?.Dispose();
}
