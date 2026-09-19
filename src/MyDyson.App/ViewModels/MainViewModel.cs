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

public partial class ZoneItem : ObservableObject
{
    public static readonly IReadOnlyList<CleanTypeOption> CleanTypeOptions =
    [
        new(CleanType.Vacuum, "Aspirer"),
        new(CleanType.Mop, "Laver"),
        new(CleanType.VacuumAndMop, "Aspirer et laver"),
        new(CleanType.VacuumThenMop, "Aspirer puis laver"),
    ];

    public string Id { get; }
    public string Name { get; }
    public string Type { get; }
    public double Area { get; }
    public ZoneMetadata Metadata { get; private set; }

    [ObservableProperty] private bool _selected;
    [ObservableProperty] private int _order;
    [ObservableProperty] private CleanTypeOption _selectedCleanType;

    public string OrderText => Selected && Order > 0 ? $"{Order}." : "";

    public ZoneItem(ZoneMetadata z)
    {
        Id = z.Id;
        Name = z.Name ?? z.Id;
        Type = z.Type ?? "";
        Area = z.Area ?? 0;
        Metadata = z;
        _selectedCleanType = CleanTypeOptions.First(o => o.Value == CleanTypes.FromRest(z.Settings?.CleanType));
    }

    /// <summary>The metadata entry with this item's current choices written back, for the REST PUT.</summary>
    public ZoneMetadata ToMetadata() => Metadata with
    {
        IsSelected = Selected,
        Order = Selected ? Order : 0,
        Settings = (Metadata.Settings ?? new ZoneSettings("auto", null, "low", 1, 1, true)) with { CleanType = SelectedCleanType.Value.ToRest() },
    };

    partial void OnSelectedChanged(bool value) => OnPropertyChanged(nameof(OrderText));
    partial void OnOrderChanged(int value) => OnPropertyChanged(nameof(OrderText));
}

public sealed record MapItem(MapMetadata Metadata)
{
    public string Id => Metadata.Id;
    public string Name => (Metadata.Name ?? Metadata.Id) + (Metadata.IsCurrentMap ? "  (active)" : "");
}

public sealed record CleanItem(CleanSummary Summary)
{
    public string When => Summary.Start?.ToLocalTime().ToString("dd.MM.yyyy HH:mm") ?? "?";
    public string Duration => Summary.CleanDurationMinutes is { } m ? $"{m} min" : "";
    public string Area => Summary.AreaCleanedSquareMetres is { } a ? $"{a:F1} m²" : "";
    public string Battery => Summary.StartBattery is { } s && Summary.EndBattery is { } e ? $"{s:F0} → {e:F0} %" : "";
    public string Faults => Summary.Faults is { Count: > 0 } f ? $"{f.Count}" : "";
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
public partial class MainViewModel : ObservableObject
{
    private readonly RobotContext _ctx;
    private readonly Dispatcher _ui;
    private readonly DispatcherTimer _refresh;
    private RobotSession? _session;
    private MapGrid? _grid;
    private string? _gridMapId;
    private PersistentMap? _map;
    private readonly Dictionary<string, PersistentMap> _mapCache = new();
    private int _nextOrder = 1;
    private bool _applyingState;
    private bool _loadingZones;

    public string RobotName => _ctx.Robot?.Name ?? "Robot";
    public string Serial => _ctx.Robot?.SerialNumber ?? "";
    public string Firmware => _ctx.Robot?.ConnectedConfiguration?.Firmware?.Version ?? "";

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
        new("ONLY_WHEN_NEEDED", "Uniquement si nécessaire", "Le robot retournera à la station d'accueil uniquement lorsqu'il devra remplir ou vider ses réservoirs"),
    ];

    public IReadOnlyList<DryOption> DryOptions { get; } =
    [
        new(3, "3 heures", "Idéal pour les stations placées dans des zones sèches et bien ventilées"),
        new(4, "4 heures", "Idéal pour les stations placées dans des zones légèrement humides"),
        new(5, "5 heures", "Idéal pour les stations placées dans des zones très humides ou peu ventilées"),
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

    private RobotPosition? _robotPosition;
    private IReadOnlyList<MyDyson.Core.Point>? _lastPath;

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
            _session = await _ctx.ConnectAsync();
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
            _session.Tracker.StateChanged += st => Post(() => ApplyState(st));
            _session.Tracker.EventReceived += (name, json) => Post(() => AddLog($"{name} {Truncate(json.ToJsonString(), 120)}"));
            Connected = true;
            Connection = "Connecté";

            await Task.WhenAll(RefreshStateAsync(), LoadMapsAsync(), LoadHistoryAsync());
            _refresh.Start();
        }
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
            LastUpdate = DateTime.Now.ToString("HH:mm:ss");

            var real = s.RealFaults.ToList();
            HasRealFault = real.Count > 0;
            FaultText = real.Count > 0
                ? string.Join(", ", real.Select(f => $"faute {f.FaultCode}" + (f.FaultCode == "589" ? " (localisation impossible)" : "")))
                : "";

            CanStart = s.IsDocked || s.State is "INACTIVE_DISCHARGING" or "FULL_CLEAN_FINISHED" or "ABORTED";
            CanPause = s.IsCleaning && !s.IsPaused;
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
            var maps = await _ctx.Api.GetMapMetadataAsync(Serial);
            Maps.Clear();
            foreach (var m in maps) Maps.Add(new MapItem(m));
            SelectedMap = Maps.FirstOrDefault(m => m.Metadata.IsCurrentMap) ?? Maps.FirstOrDefault();
        }
        catch (Exception ex) { AddLog($"cartes: {ex.Message}"); }
    }

    partial void OnSelectedMapChanged(MapItem? value)
    {
        if (value is null) return;
        _ = LoadZonesAsync(value);
    }

    private async Task LoadZonesAsync(MapItem map)
    {
        _loadingZones = true;
        try
        {
            Zones.Clear();
            _nextOrder = 1;
            foreach (var z in map.Metadata.Zones ?? [])
            {
                var item = new ZoneItem(z);
                item.PropertyChanged += (_, e) =>
                {
                    if (_loadingZones) return;
                    if (e.PropertyName == nameof(ZoneItem.Selected)) OnZoneSelectionChanged(item);
                    else if (e.PropertyName == nameof(ZoneItem.SelectedCleanType)) _ = PersistZoneSettingsAsync();
                    RebuildScene();
                };
                Zones.Add(item);
            }
            await LoadMapGeometryAsync(map.Id);
        }
        finally { _loadingZones = false; }
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

    private async Task LoadMapGeometryAsync(string mapId)
    {
        try
        {
            if (!_mapCache.TryGetValue(mapId, out var map))
            {
                map = await _ctx.Api.GetPersistentMapAsync(Serial, mapId);
                _mapCache[mapId] = map;
            }
            _map = map;
            var isCurrent = Maps.FirstOrDefault(m => m.Id == mapId)?.Metadata.IsCurrentMap == true;
            if (isCurrent && _gridMapId != mapId)
            {
                _grid = MapGrid.From(await _ctx.Api.GetMappingMapAsync(Serial));
                _gridMapId = mapId;
                var live = await _ctx.Api.GetLiveCleaningMapAsync(Serial);
                _robotPosition ??= live.RobotLocation;
                _lastPath = live.CleanPath;
            }
            RebuildScene();
        }
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
            Path = isCurrent ? _lastPath : null,
            SelectedZoneIds = Zones.Where(z => z.Selected).Select(z => z.Id).ToHashSet(),
            ZoneOrder = Zones.Where(z => z.Selected).ToDictionary(z => z.Id, z => z.Order),
        };
    }

    /// <summary>Saves the per-room clean types on the cloud so the phone app shows the same choice.</summary>
    private async Task PersistZoneSettingsAsync()
    {
        if (SelectedMap is null) return;
        try
        {
            await _ctx.Api.UpdateMapZonesAsync(Serial, SelectedMap.Id, Zones.Select(z => z.ToMetadata()).ToList());
            AddLog("réglages des pièces enregistrés");
        }
        catch (Exception ex) { AddLog($"réglages des pièces: {ex.Message}"); }
    }

    // ---- History -------------------------------------------------------------

    private async Task LoadHistoryAsync()
    {
        try
        {
            History.Clear();
            foreach (var c in await _ctx.Api.GetCleanHistoryAsync(Serial))
                History.Add(new CleanItem(c));
        }
        catch (Exception ex) { AddLog($"historique: {ex.Message}"); }
    }

    partial void OnSelectedCleanChanged(CleanItem? value)
    {
        if (value is null) { HistoryScene = new MapScene(); HistoryMapName = ""; return; }
        _ = ShowCleanAsync(value);
    }

    private IReadOnlyList<MyDyson.Core.Point>? _historyPath;
    private PersistentMap? _historyMap;

    private async Task ShowCleanAsync(CleanItem item)
    {
        try
        {
            var detail = await _ctx.Api.GetCleanDetailAsync(Serial, item.Summary.CleanId);
            _historyPath = detail.CleanPath;
            var mapId = detail.PersistentMapId ?? item.Summary.PersistentMapId;
            _historyMap = null;
            if (mapId is not null)
            {
                if (!_mapCache.TryGetValue(mapId, out var map))
                {
                    try { map = await _ctx.Api.GetPersistentMapAsync(Serial, mapId); _mapCache[mapId] = map; }
                    catch (Exception ex) { AddLog($"carte {mapId} du nettoyage: {ex.Message}"); }
                }
                _historyMap = map;
            }
            HistoryMapName = Maps.FirstOrDefault(m => m.Id == mapId)?.Metadata.Name ?? mapId ?? "carte supprimée";
            RebuildHistoryScene();
        }
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
        };
    }

    // ---- Commands ------------------------------------------------------------

    private async Task RefreshStateAsync()
    {
        if (_session is null || _session.Status != RobotConnectionStatus.Connected) return;
        try { await _session.RefreshStateAsync(); }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException) { AddLog($"état: {ex.Message}"); }
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
            await Task.Delay(1500);
            await RefreshStateAsync();
        }
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
        var mapId = long.Parse(SelectedMap.Id);
        return RunAsync($"démarrage de {rooms.Count} pièce(s)", c => CleaningSequence.StartAsync(c, mapId, rooms));
    }

    [RelayCommand] private Task PauseAsync() => RunAsync("pause", c => c.PauseAsync());

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
    [RelayCommand] private Task RefreshAsync() => Task.WhenAll(RefreshStateAsync(), LoadHistoryAsync());

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

    public async Task ShutdownAsync()
    {
        _refresh.Stop();
        await _ctx.DisposeAsync();
    }
}
