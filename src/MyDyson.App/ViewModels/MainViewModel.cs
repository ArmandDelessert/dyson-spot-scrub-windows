using System.Globalization;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MyDyson.App.Rendering;
using MyDyson.App.Services;
using MyDyson.Core;

namespace MyDyson.App.ViewModels;

public sealed record CleanTypeOption(CleanType Value, string Label);
public sealed record StrategyOption(CleaningStrategy Value, string Label);
public sealed record WaterLevelOption(WaterLevel Value, string Label);
public sealed record MopPassesOption(int Value, string Label);

public partial class ZoneItem : ObservableObject
{
    // Labels from the Android app's per-room editing screen (translated options improved: the
    // official French translation says "aspirez"/"aspirateur" inconsistently across the four
    // options; ours is uniform).
    public static readonly IReadOnlyList<CleanTypeOption> CleanTypeOptions =
    [
        new(CleanType.Vacuum, "Aspirer"),
        new(CleanType.Mop, "Laver"),
        new(CleanType.VacuumAndMop, "Aspirer et laver"),
        new(CleanType.VacuumThenMop, "Aspirer puis laver"),
    ];

    public static readonly IReadOnlyList<StrategyOption> StrategyOptions =
    [
        new(CleaningStrategy.Auto, "Auto"),
        new(CleaningStrategy.Quick, "Rapide"),
        new(CleaningStrategy.Quiet, "Silencieux"),
        new(CleaningStrategy.Boost, "Boost"),
    ];

    public static readonly IReadOnlyList<WaterLevelOption> WaterLevelOptions =
    [
        new(WaterLevel.Low, "Faible"),
        new(WaterLevel.Medium, "Moyen"),
        new(WaterLevel.High, "Élevé"),
    ];

    public static readonly IReadOnlyList<MopPassesOption> MopPassesOptions =
    [
        new(1, "1 x"),
        new(2, "2 x"),
    ];

    public string Id { get; }
    public string Name { get; }
    public string Type { get; }
    public double Area { get; }
    public ZoneMetadata Metadata { get; private set; }

    [ObservableProperty] private bool _selected;
    [ObservableProperty] private int _order;
    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private CleanTypeOption _selectedCleanType;
    [ObservableProperty] private StrategyOption _selectedStrategy;
    [ObservableProperty] private WaterLevelOption _selectedWaterLevel;
    [ObservableProperty] private MopPassesOption _selectedMopPasses;

    public string OrderText => Selected && Order > 0 ? $"{Order}." : "";

    /// <summary>What the phone app would show: the room type's own label, since it ignores the stored name for typed rooms.</summary>
    public string DisplayName => RoomTypeLabels.Resolve(string.IsNullOrEmpty(Type) ? null : Type, Name, Id);

    /// <summary>Whether the vacuum-power setting applies: every clean type except mop alone.</summary>
    public bool HasVacuum => SelectedCleanType.Value is CleanType.Vacuum or CleanType.VacuumAndMop or CleanType.VacuumThenMop;

    /// <summary>Whether the mop-only settings (water level, passes) apply to the current clean type.</summary>
    public bool HasMop => SelectedCleanType.Value is CleanType.Mop or CleanType.VacuumAndMop or CleanType.VacuumThenMop;

    /// <summary>One-line recap shown when the row is collapsed, in the phone app's own style.</summary>
    public string Summary
    {
        get
        {
            var parts = new List<string> { SelectedCleanType.Label };
            if (HasVacuum) parts.Add(SelectedStrategy.Label);
            if (HasMop) { parts.Add(SelectedWaterLevel.Label); parts.Add(SelectedMopPasses.Label); }
            return string.Join(" · ", parts);
        }
    }

    public ZoneItem(ZoneMetadata z)
    {
        Id = z.Id;
        Name = z.Name ?? z.Id;
        Type = z.Type ?? "";
        Area = z.Area ?? 0;
        Metadata = z;
        _selectedCleanType = CleanTypeOptions.First(o => o.Value == CleanTypes.FromRest(z.Settings?.CleanType));
        _selectedStrategy = StrategyOptions.First(o => o.Value == CleaningStrategies.FromRest(z.Settings?.CleaningStrategy));
        _selectedWaterLevel = WaterLevelOptions.First(o => o.Value == WaterLevels.FromRest(z.Settings?.WaterLevel));
        _selectedMopPasses = MopPassesOptions.FirstOrDefault(o => o.Value == z.Settings?.MopPasses) ?? MopPassesOptions[0];
    }

    /// <summary>The metadata entry with this item's current choices written back, for the REST PUT.</summary>
    public ZoneMetadata ToMetadata() => Metadata with
    {
        IsSelected = Selected,
        Order = Selected ? Order : 0,
        Settings = (Metadata.Settings ?? new ZoneSettings("auto", null, "low", 1, 1, true)) with
        {
            CleanType = SelectedCleanType.Value.ToRest(),
            CleaningStrategy = SelectedStrategy.Value.ToRest(),
            WaterLevel = SelectedWaterLevel.Value.ToRest(),
            MopPasses = SelectedMopPasses.Value,
        },
    };

    partial void OnSelectedChanged(bool value)
    {
        OnPropertyChanged(nameof(OrderText));
        // Expand a room's settings as soon as it is picked, collapse them again once removed;
        // the manual toggle still works independently, e.g. to peek at an unselected room.
        IsExpanded = value;
    }

    partial void OnOrderChanged(int value) => OnPropertyChanged(nameof(OrderText));

    partial void OnSelectedCleanTypeChanged(CleanTypeOption value)
    {
        OnPropertyChanged(nameof(HasVacuum));
        OnPropertyChanged(nameof(HasMop));
        OnPropertyChanged(nameof(Summary));
    }

    partial void OnSelectedStrategyChanged(StrategyOption value) => OnPropertyChanged(nameof(Summary));
    partial void OnSelectedWaterLevelChanged(WaterLevelOption value) => OnPropertyChanged(nameof(Summary));
    partial void OnSelectedMopPassesChanged(MopPassesOption value) => OnPropertyChanged(nameof(Summary));
}

public sealed record MapItem(MapMetadata Metadata)
{
    public string Id => Metadata.Id;
    public string Name => (Metadata.Name ?? Metadata.Id) + (Metadata.IsCurrentMap ? "  (active)" : "");
}

/// <summary>
/// One row of the history list. A class rather than a record: MapName and Rooms are filled in after
/// construction (see MainViewModel.FillHistoryDetailsAsync), since the accurate source for "which
/// rooms" needs a per-clean detail call the list itself doesn't carry (see Rooms's own remark).
/// </summary>
public sealed partial class CleanItem(CleanSummary Summary) : ObservableObject
{
    public CleanSummary Summary { get; } = Summary;

    public string When => Summary.Start?.ToLocalTime().ToString("dd.MM.yyyy HH:mm", CultureInfo.CurrentCulture) ?? "?";
    public string End => Summary.End?.ToLocalTime().ToString("dd.MM.yyyy HH:mm", CultureInfo.CurrentCulture) ?? "";
    public string Duration => Summary.CleanDurationMinutes is { } m ? $"{m} min" : "";
    public string Area => Summary.AreaCleanedSquareMetres is { } a ? $"{a:F1} m²" : "";
    public string Battery => Summary.StartBattery is { } s && Summary.EndBattery is { } e ? $"{s:F0} → {e:F0} %" : "";
    public string Faults => Summary.Faults is { Count: > 0 } f ? $"{f.Count}" : "";

    [ObservableProperty] private string _mapName = "";
    /// <summary>
    /// "…" until filled in from that clean's own detail. The list's own zones[].isSelected is NOT
    /// reliable here: verified against a capture that it mirrors the map's *current* room
    /// preference, not what was actually picked for this particular clean (a room excluded at
    /// launch showed isSelected true weeks... minutes later, once its selection changed again).
    /// zones[].cleanStatus, only present in the per-clean detail, is the one field confirmed to
    /// reflect this specific run (CLEAN_NOT_REQUESTED for a room not part of it).
    /// </summary>
    [ObservableProperty] private string _rooms = "…";
}

/// <summary>Consumable as shown by the app: percentage of life left, replace at 0.</summary>
public sealed record ConsumableItem(string Name, int? Usage, bool? NeedsRefill)
{
    public int Remaining => Usage is { } u ? Math.Clamp(100 - u, 0, 100) : 100;
    public string Display => Usage is { } ? $"{Remaining} %" : NeedsRefill == true ? "à recharger" : "prêt";
}

public sealed record BackWashOption(string Key, string Label, string? Description);
public sealed record DryOption(int Hours, string Label, string Description);

/// <summary>State of the dashboard. Everything the robot pushes arrives on the MQTT thread and is marshalled here.</summary>
public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly RobotContext _ctx;
    private readonly Dispatcher _ui;
    private readonly DispatcherTimer _refresh;
    private RobotSession? _session;
    private MapGrid? _grid;
    private string? _gridMapId;
    private PersistentMap? _map;
    private readonly Dictionary<string, PersistentMap> _mapCache = new();
    private readonly Dictionary<string, CleanDetail> _cleanDetailCache = new();
    private int _nextOrder = 1;
    private bool _applyingState;
    /// <summary>Cancelled by <see cref="ShutdownAsync"/> so REST calls still in flight stop instead of landing on a view model that is going away.</summary>
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationToken Ct => _lifetime.Token;

    public string RobotName => _ctx.Robot?.Name ?? "Robot";
    public string Serial => _ctx.Robot?.SerialNumber ?? "";
    public string Firmware => _ctx.Robot?.ConnectedConfiguration?.Firmware?.Version ?? "";
    /// <summary>The only account information the login flow ever returns: no display name, just the email used to sign in.</summary>
    public string AccountEmail => _ctx.Stored.Email;

    [ObservableProperty] private string _connection = "Connexion…";
    [ObservableProperty] private bool _connected;
    [ObservableProperty] private string _stateText = "";
    [ObservableProperty] private string _actionText = "";
    [ObservableProperty] private string _dockText = "";
    [ObservableProperty] private int _battery;
    [ObservableProperty] private string _faultText = "";
    [ObservableProperty] private bool _hasRealFault;
    [ObservableProperty] private string _lastUpdate = "";
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private bool _busy;

    [ObservableProperty] private bool _canStart;
    [ObservableProperty] private bool _canPause;
    [ObservableProperty] private bool _canAbort;
    [ObservableProperty] private bool _dockBusy;
    [ObservableProperty] private string _washDryLabel = "Laver et sécher";
    [ObservableProperty] private string _pauseResumeLabel = "⏸ Pause";
    private string? _dockState;

    // Settings (wording of the official app)
    [ObservableProperty] private bool _hotWaterMop;
    [ObservableProperty] private bool _detergent;
    [ObservableProperty] private string _solutionStatus = "";
    [ObservableProperty] private bool _hotWaterSwitch;
    [ObservableProperty] private BackWashOption? _backWash;
    [ObservableProperty] private DryOption? _dryDuration;
    [ObservableProperty] private bool _alarm;
    [ObservableProperty] private int _volume;
    [ObservableProperty] private bool _washMopBeforeClean;
    [ObservableProperty] private bool _washMopBeforeCleanKnown;

    public IReadOnlyList<BackWashOption> BackWashOptions { get; } =
    [
        new("ROOM", "Après chaque pièce", null),
        new("TIME15", "Toutes les 15 min", null),
        new("TIME30", "Toutes les 30 min", null),
        new("ONLY_WHEN_NEEDED", "Uniquement si nécessaire", "Le robot retournera à la station d'accueil uniquement lorsqu'il devra remplir ou vider ses réservoirs."),
    ];

    public IReadOnlyList<DryOption> DryOptions { get; } =
    [
        new(3, "3 heures", "Idéal pour les stations placées dans des zones sèches et bien ventilées."),
        new(4, "4 heures", "Idéal pour les stations placées dans des zones légèrement humides."),
        new(5, "5 heures", "Idéal pour les stations placées dans des zones très humides ou peu ventilées."),
    ];

    public ObservableCollection<MapItem> Maps { get; } = new();
    [ObservableProperty] private MapItem? _selectedMap;
    public ObservableCollection<ZoneItem> Zones { get; } = new();
    public ObservableCollection<ConsumableItem> Consumables { get; } = new();
    public ObservableCollection<CleanItem> History { get; } = new();
    public ObservableCollection<string> Log { get; } = new();
    [ObservableProperty] private CleanItem? _selectedClean;
    [ObservableProperty] private MapScene _scene = new();
    [ObservableProperty] private MapScene _historyScene = new();
    [ObservableProperty] private string _historyMapName = "";
    [ObservableProperty] private string _historyResultText = "";

    private RobotPosition? _robotPosition;
    private IReadOnlyList<MyDyson.Core.Point>? _lastPath;
    /// <summary>Grown in real time from the jdm "cur_path" push (see RobotStateTracker.CleanPath); takes over from the REST snapshot in _lastPath as soon as it has any points.</summary>
    private IReadOnlyList<MyDyson.Core.Point>? _liveTrail;
    private IReadOnlyList<MyDyson.Core.Point>? _liveObstacles;
    private IReadOnlyList<DirtSpot>? _liveDirt;

    // ---- Raw capture: every message on the robot's topics, for finding what the tracker doesn't know ----
    [ObservableProperty] private bool _isCapturing;
    [ObservableProperty] private string _captureButtonLabel = "Capturer tous les messages…";
    [ObservableProperty] private string _captureInfo = "";
    private System.IO.StreamWriter? _captureWriter;
    private readonly object _captureLock = new();
    private string? _captureFileName;
    private int _captureCount;

    public event Action? LoggedOut;
    /// <summary>A robot event worth a Windows notification: title, body.</summary>
    public event Action<string, string>? NotifyRequested;

    public MainViewModel(RobotContext ctx)
    {
        _ctx = ctx;
        _ui = Application.Current.Dispatcher;
        _ctx.Log += m => Post(() => AddLog(m));
        _refresh = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _refresh.Tick += async (_, _) => await RefreshStateAsync();
        ThemeService.Changed += () => Post(() => { RebuildScene(); RebuildHistoryScene(); });
    }

    private void Post(Action a)
    {
        if (_ui.CheckAccess()) a(); else _ui.BeginInvoke(a);
    }

    private void AddLog(string line)
    {
        Log.Insert(0, $"{DateTime.Now:HH:mm:ss} {line}");
        while (Log.Count > 200) Log.RemoveAt(Log.Count - 1);
    }

    public async Task StartAsync()
    {
        try
        {
            _session = await _ctx.ConnectAsync(Ct);
            _session.ConnectionChanged += (s, d) => Post(() =>
            {
                Connected = s == RobotConnectionStatus.Connected;
                Connection = s switch
                {
                    RobotConnectionStatus.Connected => "Connecté",
                    RobotConnectionStatus.Connecting => "Connexion…",
                    RobotConnectionStatus.Reconnecting => "Reconnexion…" + (d is null ? "" : $" ({d})"),
                    _ => "Déconnecté",
                };
            });
            _session.AuthenticationLost += reason => Post(() => _ = SessionExpiredAsync(reason));
            _session.Tracker.StateChanged += st => Post(() => ApplyState(st));
            _session.Tracker.CleanPathChanged += path => Post(() =>
            {
                _liveTrail = path.Select(p => new MyDyson.Core.Point(p.X, p.Y, p.Update)).ToList();
                RebuildScene();
            });
            _session.Tracker.EventReceived += (name, json) => Post(() =>
            {
                AddLog($"{name} {Truncate(json.ToJsonString(), 120)}");
                switch (name)
                {
                    case "event.clean_finish.post":
                        NotifyRequested?.Invoke("Nettoyage terminé", "Le robot a terminé son nettoyage.");
                        break;
                    case "event.Unable_all_area_recharge.post":
                        NotifyRequested?.Invoke("Zone inaccessible", "Le robot n'a pas pu atteindre une ou plusieurs pièces sélectionnées.");
                        break;
                }
            });
            // Every message on the robot's topics, regardless of whether the tracker recognises it;
            // only written anywhere once a capture file has been opened (see ToggleCaptureCommand).
            _session.MessageReceived += CaptureMessage;
            Connected = true;
            Connection = "Connecté";

            await Task.WhenAll(RefreshStateAsync(), LoadMapsAsync(), LoadHistoryAsync());
            _refresh.Start();
            _ = FillHistoryDetailsAsync();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Message = ex.Message;
            AddLog(ex.Message);
        }
    }

    // ---- State ---------------------------------------------------------------

    private void ApplyState(RobotState s)
    {
        _applyingState = true;
        try
        {
            StateText = Describe(s.State);
            ActionText = s.FullCleanAction switch
            {
                "VACUUMING" => "aspiration",
                "VACUUMING_AND_MOPPING" => "aspiration et lavage",
                "MOPPING" => "lavage",
                null or "NONE" => "",
                var a => a,
            };
            _dockState = s.DockState;
            DockText = DescribeDock(s.DockState);
            DockBusy = s.IsDockBusy;
            WashDryLabel = s.DockState switch
            {
                "WASHING_MOP" => "Arrêter le lavage",
                "DRYING_MOP" => "Arrêter le séchage",
                "COLLECTING_DUST" => "Arrêter le vidage",
                _ => "Laver et sécher",
            };
            if (s.BatteryChargeLevel is { } b) Battery = b;
            LastUpdate = DateTime.Now.ToString("HH:mm:ss", CultureInfo.CurrentCulture);

            var real = s.RealFaults.ToList();
            HasRealFault = real.Count > 0;
            FaultText = real.Count > 0
                ? string.Join(", ", real.Select(f => $"faute {f.FaultCode}" + (f.FaultCode == "589" ? " (localisation impossible)" : "")))
                : "";

            CanStart = s.IsDocked || s.State is "INACTIVE_DISCHARGING" or "FULL_CLEAN_FINISHED" or "ABORTED";
            CanPause = s.IsCleaning; // true whether running or already paused: this is the pause/resume toggle
            PauseResumeLabel = s.IsPaused ? "▶ Reprendre" : "⏸ Pause";
            CanAbort = s.IsCleaning || s.IsPaused || s.IsMapping;

            if (s.HotWaterMop is { } hwm) HotWaterMop = hwm;
            if (s.HotWaterSwitch is { } hws) HotWaterSwitch = hws;
            if (s.Detergent is { } det) Detergent = det;
            if (s.Alarm is { } al) Alarm = al;
            if (s.Volume is { } vol) Volume = vol;
            if (s.WashMopBeforeClean is { } wm) { WashMopBeforeClean = wm; WashMopBeforeCleanKnown = true; }
            if (s.AirDryFrequency is { } adf) DryDuration = DryOptions.FirstOrDefault(o => o.Hours == adf) ?? DryDuration;
            BackWash = s.BackWashType switch
            {
                "ROOM" => BackWashOptions[0],
                "TIME" when s.BackWashTime is 30 => BackWashOptions[2],
                "TIME" => BackWashOptions[1],
                "ONLY_WHEN_NEEDED" => BackWashOptions[3],
                _ => BackWash,
            };

            if (s.Consumables is { } cons)
            {
                Consumables.Clear();
                foreach (var c in cons)
                    Consumables.Add(new ConsumableItem(DescribeConsumable(c.Type), c.Usage, c.NeedsRefill));
                var solution = cons.FirstOrDefault(c => c.Type == "cleaningSolution");
                SolutionStatus = solution is null ? "" : solution.NeedsRefill == true ? "À recharger" : "Prêt à l'emploi";
            }

            _robotPosition = s.LatestPosition ?? _robotPosition;
            RebuildScene();
        }
        finally
        {
            _applyingState = false;
        }
    }

    // ---- Maps and zones -------------------------------------------------------

    private async Task LoadMapsAsync()
    {
        try
        {
            var maps = await _ctx.Api.GetMapMetadataAsync(Serial, Ct);
            Maps.Clear();
            foreach (var m in maps) Maps.Add(new MapItem(m));
            SelectedMap = Maps.FirstOrDefault(m => m.Metadata.IsCurrentMap) ?? Maps.FirstOrDefault();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) { AddLog($"cartes: {ex.Message}"); }
    }

    partial void OnSelectedMapChanged(MapItem? value)
    {
        if (value is null) return;
        _ = LoadZonesAsync(value);
    }

    private async Task LoadZonesAsync(MapItem map)
    {
        Zones.Clear();
        _nextOrder = 1;
        // The cloud API returns zones in an unexplained order (probably the order the robot
        // detected them in while mapping), neither by id nor alphabetical. Sorting by name
        // gives a predictable list; it will not exactly match the phone app, which appears to
        // group by room type rather than sort by name.
        var ordered = (map.Metadata.Zones ?? []).OrderBy(z => z.Name, StringComparer.Create(new System.Globalization.CultureInfo("fr-FR"), ignoreCase: true));
        foreach (var z in ordered)
        {
            // Subscribed after construction, so only the user's edits land here — including any
            // made while the geometry below is still downloading, which must not be lost.
            var item = new ZoneItem(z);
            item.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ZoneItem.Selected)) OnZoneSelectionChanged(item);
                else if (e.PropertyName is nameof(ZoneItem.SelectedCleanType) or nameof(ZoneItem.SelectedStrategy)
                         or nameof(ZoneItem.SelectedWaterLevel) or nameof(ZoneItem.SelectedMopPasses))
                    ScheduleZoneSettingsPersist();
                RebuildScene();
            };
            Zones.Add(item);
        }
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

    /// <summary>Called by the map view when a room is clicked.</summary>
    public void ToggleZone(string zoneId)
    {
        var z = Zones.FirstOrDefault(z => z.Id == zoneId);
        if (z is not null) z.Selected = !z.Selected;
    }

    /// <summary>Called by the map view when the user clicks/taps empty map space.</summary>
    public void ClearSelection()
    {
        foreach (var z in Zones) z.Selected = false;
    }

    private async Task LoadMapGeometryAsync(string mapId)
    {
        try
        {
            if (!_mapCache.TryGetValue(mapId, out var map))
            {
                map = await _ctx.Api.GetPersistentMapAsync(Serial, mapId, Ct);
                _mapCache[mapId] = map;
            }
            _map = map;
            var isCurrent = Maps.FirstOrDefault(m => m.Id == mapId)?.Metadata.IsCurrentMap == true;
            if (isCurrent && _gridMapId != mapId)
            {
                _grid = MapGrid.From(await _ctx.Api.GetMappingMapAsync(Serial, Ct));
                _gridMapId = mapId;
                var live = await _ctx.Api.GetLiveCleaningMapAsync(Serial, Ct);
                _robotPosition ??= live.RobotLocation;
                _lastPath = live.CleanPath;
                _liveObstacles = live.Obstacles;
                _liveDirt = live.Dirt;
            }
            RebuildScene();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) { AddLog($"carte {mapId}: {ex.Message}"); }
    }

    private void RebuildScene()
    {
        var mapId = SelectedMap?.Id;
        var isCurrent = SelectedMap?.Metadata.IsCurrentMap == true;
        Scene = new MapScene
        {
            Grid = isCurrent && _gridMapId == mapId ? _grid : null,
            Map = _map?.Id == mapId ? _map : null,
            ZoneMetadata = SelectedMap?.Metadata.Zones,
            Dock = _map?.Id == mapId ? _map?.DockLocation : null,
            Robot = isCurrent ? _robotPosition : null,
            Path = isCurrent ? (_liveTrail is { Count: > 0 } ? _liveTrail : _lastPath) : null,
            Obstacles = isCurrent ? _liveObstacles : null,
            DirtSpots = isCurrent ? _liveDirt : null,
            SelectedZoneIds = Zones.Where(z => z.Selected).Select(z => z.Id).ToHashSet(),
            ZoneOrder = Zones.Where(z => z.Selected).ToDictionary(z => z.Id, z => z.Order),
        };
    }

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
        var pending = _persistZonesPending = CancellationTokenSource.CreateLinkedTokenSource(Ct);
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
                await _ctx.Api.UpdateMapZonesAsync(Serial, map.Id, zones.Select(z => z.ToMetadata()).ToList(), Ct);
                AddLog("réglages des pièces enregistrés");
            }
            finally { _persistZonesLock.Release(); }
        }
        catch (OperationCanceledException) when (pending.IsCancellationRequested) { } // superseded, or shutting down
        catch (Exception ex) { AddLog($"réglages des pièces: {ex.Message}"); }
    }

    // ---- History -------------------------------------------------------------

    private async Task LoadHistoryAsync()
    {
        try
        {
            History.Clear();
            foreach (var c in await _ctx.Api.GetCleanHistoryAsync(Serial, Ct))
                History.Add(new CleanItem(c));
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) { AddLog($"historique: {ex.Message}"); }
    }

    /// <summary>
    /// Resolves each history row's map name (cheap, from the already-loaded Maps) and, one at a time
    /// so as not to fetch several hundred KB per entry all at once, its actual room list from that
    /// clean's own detail. Called after Maps and History have both loaded.
    /// </summary>
    private async Task FillHistoryDetailsAsync()
    {
        foreach (var item in History)
            item.MapName = Maps.FirstOrDefault(m => m.Id == item.Summary.PersistentMapId)?.Metadata.Name
                ?? item.Summary.PersistentMapId ?? "";

        foreach (var item in History)
        {
            try
            {
                var detail = await GetCleanDetailCachedAsync(item.Summary.CleanId);
                item.Rooms = DescribeRooms(detail);
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return; }
            catch (Exception ex) { item.Rooms = ""; AddLog($"pièces du nettoyage {item.Summary.CleanId}: {ex.Message}"); }
        }
    }

    private async Task<CleanDetail> GetCleanDetailCachedAsync(string cleanId)
    {
        if (!_cleanDetailCache.TryGetValue(cleanId, out var detail))
            _cleanDetailCache[cleanId] = detail = await _ctx.Api.GetCleanDetailAsync(Serial, cleanId, Ct);
        return detail;
    }

    /// <summary>
    /// The rooms actually part of a clean. zones[].isSelected mirrors the map's *current* room
    /// preference rather than what was picked for this specific task (confirmed against a capture:
    /// a room excluded at launch still showed isSelected true once its selection later changed), so
    /// cleanStatus is used instead — CLEAN_NOT_REQUESTED is the one value confirmed to reflect this
    /// particular run rather than the map's present state.
    /// </summary>
    private static string DescribeRooms(CleanDetail detail) => string.Join(", ", (detail.Zones ?? [])
        .Where(z => z.CleanStatus is not (null or "CLEAN_NOT_REQUESTED"))
        .Select(z => RoomTypeLabels.Resolve(z.Type, z.Name, z.Id)));

    partial void OnSelectedCleanChanged(CleanItem? value)
    {
        if (value is null) { HistoryScene = new MapScene(); HistoryMapName = ""; HistoryResultText = ""; return; }
        _ = ShowCleanAsync(value);
    }

    private IReadOnlyList<MyDyson.Core.Point>? _historyPath;
    private IReadOnlyList<MyDyson.Core.Point>? _historyObstacles;
    private IReadOnlyList<DirtSpot>? _historyDirt;
    private PersistentMap? _historyMap;

    private async Task ShowCleanAsync(CleanItem item)
    {
        try
        {
            var detail = await GetCleanDetailCachedAsync(item.Summary.CleanId);
            _historyPath = detail.CleanPath;
            _historyObstacles = detail.Obstacles;
            _historyDirt = detail.Dirt;
            // The REST clean list has no overall success/failure field (see docs/protocole.md); the
            // closest thing is each zone's own status from this per-clean detail call. Only surface
            // zones that didn't simply complete, so an ordinary clean just reads "Terminé".
            var problems = detail.Zones?
                .Where(z => z.CleanStatus is not (null or "CLEAN_NOT_REQUESTED" or "CLEAN_COMPLETE"))
                .Select(z => $"{RoomTypeLabels.Resolve(z.Type, z.Name, z.Id)} : {CleanStatusLabels.Resolve(z.CleanStatus)}")
                .ToList() ?? [];
            HistoryResultText = problems.Count > 0 ? string.Join(", ", problems) : "Terminé";
            item.Rooms = DescribeRooms(detail);
            var mapId = detail.PersistentMapId ?? item.Summary.PersistentMapId;
            _historyMap = null;
            if (mapId is not null)
            {
                if (!_mapCache.TryGetValue(mapId, out var map))
                {
                    try { map = await _ctx.Api.GetPersistentMapAsync(Serial, mapId, Ct); _mapCache[mapId] = map; }
                    catch (Exception ex) when (!_lifetime.IsCancellationRequested) { AddLog($"carte {mapId} du nettoyage: {ex.Message}"); }
                }
                _historyMap = map;
            }
            HistoryMapName = Maps.FirstOrDefault(m => m.Id == mapId)?.Metadata.Name ?? mapId ?? "carte supprimée";
            RebuildHistoryScene();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) { AddLog($"nettoyage: {ex.Message}"); }
    }

    private void RebuildHistoryScene()
    {
        var mapId = _historyMap?.Id;
        HistoryScene = new MapScene
        {
            Grid = mapId is not null && _gridMapId == mapId ? _grid : null,
            Map = _historyMap,
            ZoneMetadata = Maps.FirstOrDefault(m => m.Id == mapId)?.Metadata.Zones,
            Dock = _historyMap?.DockLocation,
            Path = _historyPath,
            Obstacles = _historyObstacles,
            DirtSpots = _historyDirt,
        };
    }

    // ---- Commands ------------------------------------------------------------

    private async Task RefreshStateAsync()
    {
        if (_session is null || _session.Status != RobotConnectionStatus.Connected) return;
        // Runs from an async-void timer tick: anything escaping here is an unhandled exception that
        // ends the process, and a periodic refresh is never worth that, whatever went wrong.
        try { await _session.RefreshStateAsync(Ct); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) { AddLog($"état: {ex.Message}"); }
    }

    private async Task RunAsync(string label, Func<RobotMqttClient, Task> action)
    {
        if (_session?.Client is not { IsConnected: true } client) { Message = "Robot non connecté."; return; }
        Busy = true;
        Message = "";
        try
        {
            await action(client);
            AddLog(label);
            await Task.Delay(1500, Ct);
            await RefreshStateAsync();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Message = ex.Message;
            AddLog($"{label}: {ex.Message}");
        }
        finally { Busy = false; }
    }

    [RelayCommand]
    private Task StartCleanAsync()
    {
        var rooms = Zones.Where(z => z.Selected).OrderBy(z => z.Order)
            .Select(z => new RoomSelection(z.Id, z.SelectedCleanType.Value, z.Order)).ToList();
        if (rooms.Count == 0 || SelectedMap is null) { Message = "Sélectionnez au moins une pièce."; return Task.CompletedTask; }
        // Outside RunAsync's try, so a non-numeric id must not throw: that would surface as an
        // unhandled exception in the command dispatch rather than a message.
        if (!long.TryParse(SelectedMap.Id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var mapId))
        {
            Message = $"Identifiant de carte inattendu : {SelectedMap.Id}";
            return Task.CompletedTask;
        }
        return RunAsync($"démarrage de {rooms.Count} pièce(s)", c => CleaningSequence.StartAsync(c, mapId, rooms));
    }

    [RelayCommand]
    private Task PauseResumeAsync() => _session?.Tracker.State?.IsPaused == true
        ? RunAsync("reprise", c => c.ResumeAsync(_session!.Tracker.State?.CurrentCleaningMode ?? "zoneConfigured"))
        : RunAsync("pause", c => c.PauseAsync());

    [RelayCommand]
    private Task AbortAsync() => RunAsync("retour à la station", async c =>
    {
        var st = _session!.Tracker.State;
        await c.AbortAsync(st?.State ?? "FULL_CLEAN_RUNNING", st?.CurrentCleaningMode ?? "zoneConfigured");
    });

    [RelayCommand]
    private Task WashDryAsync() => DockBusy
        ? RunAsync("arrêt de l'action de la station", c => c.StopDockActionAsync(_dockState))
        : RunAsync("laver et sécher", c => c.WashAndDryMopAsync());

    [RelayCommand] private Task CollectDustAsync() => RunAsync("vidage du collecteur", c => c.CollectDustAsync());
    [RelayCommand]
    private async Task RefreshAsync()
    {
        await Task.WhenAll(RefreshStateAsync(), LoadMapsAsync(), LoadHistoryAsync());
        _ = FillHistoryDetailsAsync();
    }

    /// <summary>Makes the selected map the account's active map, as the phone app does from its map picker.</summary>
    [RelayCommand]
    private async Task SetActiveMapAsync()
    {
        if (SelectedMap is not { } map || map.Metadata.IsCurrentMap) return;
        await RunAsync($"carte active : {map.Metadata.Name}", c => c.SetCurrentMapAsync(long.Parse(map.Id, CultureInfo.InvariantCulture)));
        await LoadMapsAsync();
    }

    [RelayCommand]
    private void ExportMap()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog { Filter = "Image PNG|*.png", FileName = $"carte-{SelectedMap?.Metadata.Name ?? "robot"}.png" };
        if (dlg.ShowDialog() == true)
        {
            MapRenderer.ExportPng(Scene, 1200, 1400, dlg.FileName);
            AddLog($"carte exportée vers {dlg.FileName}");
        }
    }

    // Settings: only react to user changes, not to values coming from the robot.
    partial void OnHotWaterMopChanged(bool value) { if (!_applyingState) _ = RunAsync("laver à l'eau chaude", c => c.SetHotWaterMopAsync(value)); }
    partial void OnDetergentChanged(bool value) { if (!_applyingState) _ = RunAsync("laver avec le produit", c => c.SetDetergentAsync(value)); }
    partial void OnHotWaterSwitchChanged(bool value) { if (!_applyingState) _ = RunAsync("autonettoyage à l'eau chaude", c => c.SetHotWaterSwitchAsync(value)); }
    partial void OnAlarmChanged(bool value) { if (!_applyingState) _ = RunAsync("sons", c => c.SetAlarmAsync(value)); }
    partial void OnWashMopBeforeCleanChanged(bool value) { if (!_applyingState) _ = RunAsync("prolonger les préparatifs (message classique seul)", c => c.SetWashMopBeforeCleanAsync(value)); }
    partial void OnDryDurationChanged(DryOption? value) { if (!_applyingState && value is not null) _ = RunAsync($"séchage {value.Hours} h", c => c.SetAirDryFrequencyAsync(value.Hours)); }
    partial void OnBackWashChanged(BackWashOption? value)
    {
        if (_applyingState || value is null) return;
        _ = RunAsync($"intervalle: {value.Label}", c => value.Key switch
        {
            "ROOM" => c.SetBackWashPerRoomAsync(),
            "TIME15" => c.SetBackWashByTimeAsync(15),
            "TIME30" => c.SetBackWashByTimeAsync(30),
            _ => c.SetBackWashOnlyWhenNeededAsync(),
        });
    }

    [RelayCommand] private Task ApplyVolumeAsync() => RunAsync($"volume {Volume}", c => c.SetVolumeAsync(Volume));

    // ---- Text helpers ---------------------------------------------------------

    private static string Describe(string? state) => state switch
    {
        "INACTIVE_CHARGING" => "En charge sur la station",
        "INACTIVE_CHARGED" => "Chargé, sur la station",
        "INACTIVE_DISCHARGING" => "Au repos, hors station",
        "FULL_CLEAN_INITIATED" or "FULL_CLEAN_STARTING" => "Démarrage",
        "FULL_CLEAN_DISCOVERING" => "Localisation",
        "FULL_CLEAN_RUNNING" => "Nettoyage en cours",
        "FULL_CLEAN_PAUSED" => "En pause",
        "FULL_CLEAN_PAUSING" => "Mise en pause",
        "FULL_CLEAN_RESUMING" => "Reprise",
        "FULL_CLEAN_CHARGING" => "Recharge avant de continuer",
        "FULL_CLEAN_NEEDS_CHARGE" => "Batterie insuffisante",
        "FULL_CLEAN_FINISHED" => "Nettoyage terminé",
        "FULL_CLEAN_ABORTED" or "ABORTED" => "Nettoyage abandonné",
        "FULL_CLEAN_ABANDONED" => "Nettoyage interrompu",
        "MAPPING_RUNNING" => "Cartographie en cours",
        "MAPPING_FINISHED" => "Cartographie terminée",
        null => "",
        var s => s,
    };

    private static string DescribeDock(string? dock) => dock switch
    {
        "IDLE" or null => "",
        "WASHING_MOP" => "Station : lavage du rouleau humide",
        "DRYING_MOP" => "Station : séchage du rouleau humide",
        "COLLECTING_DUST" => "Station : vidage du collecteur",
        var d => $"Station : {d}",
    };

    private static string DescribeConsumable(string type) => type switch
    {
        "brushBar" => "Brosse",
        "mopRoller" => "Rouleau humide",
        "sideBrushes" => "Brosses latérales",
        "robotFilter" => "Filtre du robot",
        "dockFilter" => "Filtre de la station",
        "ioniserCartridge" => "Cartouche ioniseur",
        "cleaningSolution" => "Produit de nettoyage",
        var t => t,
    };

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";

    /// <summary>For screenshots: selects the most recent clean.</summary>
    public void SelectFirstClean() => SelectedClean = History.FirstOrDefault();

    // ---- Raw capture ----------------------------------------------------------

    /// <summary>
    /// Writes every message the robot exchanges, one JSON object per line, in the same shape the
    /// CLI's "watch --log" produces. The Journal tab only ever shows a curated subset (recognised
    /// events and our own command results); this is the way to see everything, including message
    /// types nothing in this app understands yet.
    /// </summary>
    private void CaptureMessage(RobotMessage m)
    {
        System.IO.StreamWriter? w;
        lock (_captureLock) { w = _captureWriter; }
        if (w is null) return;

        var line = System.Text.Json.JsonSerializer.Serialize(new
        {
            time = m.ReceivedUtc,
            topic = m.Topic,
            payload = (object?)m.Json ?? m.Payload,
        });
        lock (_captureLock) { _captureWriter?.WriteLine(line); }
        _captureCount++;
        Post(() => CaptureInfo = $"{_captureCount} message(s) → {_captureFileName}");
    }

    [RelayCommand]
    private void ToggleCapture()
    {
        if (IsCapturing) { StopCapture(); return; }

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "JSON Lines (*.jsonl)|*.jsonl",
            FileName = $"capture-{DateTime.Now:yyyy-MM-dd-HH-mm-ss}.jsonl",
        };
        if (dlg.ShowDialog() != true) return;

        lock (_captureLock)
        {
            // No byte order mark: a BOM at the head of a JSON Lines file breaks standard JSON parsers.
            _captureWriter = new System.IO.StreamWriter(dlg.FileName, append: false, new System.Text.UTF8Encoding(false)) { AutoFlush = true };
            _captureCount = 0;
        }
        _captureFileName = System.IO.Path.GetFileName(dlg.FileName);
        IsCapturing = true;
        CaptureButtonLabel = "Arrêter la capture";
        CaptureInfo = $"0 message(s) → {_captureFileName}";
        AddLog($"capture démarrée : {dlg.FileName}");
    }

    private void StopCapture()
    {
        System.IO.StreamWriter? w;
        lock (_captureLock) { w = _captureWriter; _captureWriter = null; }
        w?.Dispose();
        IsCapturing = false;
        CaptureButtonLabel = "Capturer tous les messages…";
        CaptureInfo = _captureCount > 0 ? $"Dernière capture : {_captureCount} message(s) dans {_captureFileName}" : "";
        AddLog($"capture arrêtée, {_captureCount} message(s) enregistré(s)");
    }

    // ---- Account ---------------------------------------------------------------

    [RelayCommand]
    private async Task LogoutAsync()
    {
        var result = MessageBox.Show(
            "Vous devrez ressaisir votre e-mail, votre mot de passe et un code reçu par e-mail pour vous reconnecter.\n\nSe déconnecter du compte MyDyson ?",
            "Déconnexion",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes) return;

        await ShutdownAsync();
        SessionStore.Delete();
        LoggedOut?.Invoke();
    }

    /// <summary>The token died mid-session: same exit as a logout, but told rather than asked.</summary>
    private async Task SessionExpiredAsync(string reason)
    {
        AddLog($"session expirée: {reason}");
        MessageBox.Show(
            "La session MyDyson n'est plus acceptée par le cloud Dyson. Vous devez vous reconnecter.",
            "Session expirée", MessageBoxButton.OK, MessageBoxImage.Warning);
        await ShutdownAsync();
        SessionStore.Delete();
        LoggedOut?.Invoke();
    }

    public async Task ShutdownAsync()
    {
        _refresh.Stop();
        _lifetime.Cancel();
        StopCaptureIfAny();
        await _ctx.DisposeAsync();
    }

    private void StopCaptureIfAny()
    {
        if (IsCapturing) StopCapture();
    }

    /// <summary>
    /// Owns the capture file and its cancellation sources; the robot context is released by
    /// <see cref="ShutdownAsync"/>. The persist lock is left alone on purpose: a PUT cancelled by
    /// the lifetime token still releases it from a later continuation, and an unused SemaphoreSlim
    /// holds nothing that needs disposing.
    /// </summary>
    public void Dispose()
    {
        _refresh.Stop();
        _lifetime.Cancel();
        StopCaptureIfAny();
        _persistZonesPending?.Dispose();
        _lifetime.Dispose();
    }
}
