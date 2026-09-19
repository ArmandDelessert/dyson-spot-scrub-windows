using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MyDyson.App.Rendering;
using MyDyson.App.Services;
using MyDyson.Core;

namespace MyDyson.App.ViewModels;

public partial class ZoneItem : ObservableObject
{
    public string Id { get; }
    public string Name { get; }
    public string Type { get; }
    public double Area { get; }
    [ObservableProperty] private bool _selected;

    public ZoneItem(ZoneMetadata z)
    {
        Id = z.Id;
        Name = z.Name ?? z.Id;
        Type = z.Type ?? "";
        Area = z.Area ?? 0;
        _selected = z.IsSelected ?? false;
    }
}

public sealed record CleanItem(CleanSummary Summary)
{
    public string When => Summary.Start?.ToLocalTime().ToString("dd.MM.yyyy HH:mm") ?? "?";
    public string Duration => Summary.CleanDurationMinutes is { } m ? $"{m} min" : "";
    public string Area => Summary.AreaCleanedSquareMetres is { } a ? $"{a:F1} m²" : "";
    public string Battery => Summary.StartBattery is { } s && Summary.EndBattery is { } e ? $"{s:F0} → {e:F0} %" : "";
    public string Faults => Summary.Faults is { Count: > 0 } f ? $"{f.Count} faute(s)" : "";
}

public sealed record ConsumableItem(string Name, int? Usage, bool? NeedsRefill)
{
    public string Display => Usage is { } u ? $"{u} %" : NeedsRefill == true ? "à recharger" : "ok";
    public double Bar => Usage ?? 0;
}

/// <summary>State of the dashboard. Everything the robot pushes arrives on the MQTT thread and is marshalled here.</summary>
public partial class MainViewModel : ObservableObject
{
    private readonly RobotContext _ctx;
    private readonly Dispatcher _ui;
    private readonly DispatcherTimer _refresh;
    private RobotSession? _session;
    private MapGrid? _grid;
    private PersistentMap? _map;
    private List<MapMetadata> _maps = new();

    public string RobotName => _ctx.Robot?.Name ?? "Robot";
    public string Serial => _ctx.Robot?.SerialNumber ?? "";
    public string Firmware => _ctx.Robot?.ConnectedConfiguration?.Firmware?.Version ?? "";

    [ObservableProperty] private string _connection = "Connexion…";
    [ObservableProperty] private bool _connected;
    [ObservableProperty] private string _stateText = "";
    [ObservableProperty] private string _dockText = "";
    [ObservableProperty] private string _actionText = "";
    [ObservableProperty] private int _battery;
    [ObservableProperty] private string _faultText = "";
    [ObservableProperty] private bool _hasRealFault;
    [ObservableProperty] private string _lastUpdate = "";
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private bool _busy;

    [ObservableProperty] private bool _canStart;
    [ObservableProperty] private bool _canPause;
    [ObservableProperty] private bool _canAbort;
    [ObservableProperty] private bool _isDrying;

    // Settings mirrored from CURRENT-STATE; writes go through the robot.
    [ObservableProperty] private bool _hotWaterMop;
    [ObservableProperty] private bool _hotWaterSwitch;
    [ObservableProperty] private bool _detergent;
    [ObservableProperty] private bool _alarm;
    [ObservableProperty] private int _volume;
    [ObservableProperty] private int _airDryFrequency = 3;
    [ObservableProperty] private string _backWashText = "";
    private bool _applyingState;

    public ObservableCollection<ZoneItem> Zones { get; } = new();
    public ObservableCollection<ConsumableItem> Consumables { get; } = new();
    public ObservableCollection<CleanItem> History { get; } = new();
    public ObservableCollection<string> Log { get; } = new();
    [ObservableProperty] private CleanItem? _selectedClean;
    [ObservableProperty] private MapScene _scene = new();
    [ObservableProperty] private string _currentMapName = "";

    public MainViewModel(RobotContext ctx)
    {
        _ctx = ctx;
        _ui = Application.Current.Dispatcher;
        _ctx.Log += m => Post(() => AddLog(m));
        _refresh = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _refresh.Tick += async (_, _) => await RefreshStateAsync();
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

    private void ApplyState(RobotState s)
    {
        _applyingState = true;
        try
        {
            StateText = Describe(s.State);
            DockText = DescribeDock(s.DockState);
            ActionText = s.FullCleanAction switch
            {
                "VACUUMING" => "aspiration",
                "VACUUMING_AND_MOPPING" => "aspiration et serpillière",
                "MOPPING" => "serpillière",
                null or "NONE" => "",
                var a => a,
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
            IsDrying = s.DockState == "DRYING_MOP";

            if (s.HotWaterMop is { } hwm) HotWaterMop = hwm;
            if (s.HotWaterSwitch is { } hws) HotWaterSwitch = hws;
            if (s.Detergent is { } det) Detergent = det;
            if (s.Alarm is { } al) Alarm = al;
            if (s.Volume is { } vol) Volume = vol;
            if (s.AirDryFrequency is { } adf) AirDryFrequency = adf;
            BackWashText = s.BackWashType == "ROOM" ? "après chaque pièce" : s.BackWashTime is { } t ? $"toutes les {t} min" : "";

            if (s.Consumables is { } cons)
            {
                Consumables.Clear();
                foreach (var c in cons)
                    Consumables.Add(new ConsumableItem(DescribeConsumable(c.Type), c.Usage, c.NeedsRefill));
            }

            if (s.PersistentMapId is { } mapId && mapId != "0" && _map?.Id != mapId && _maps.Any(m => m.Id == mapId))
                _ = LoadMapGeometryAsync(mapId);

            RebuildScene(s.LatestPosition);
        }
        finally
        {
            _applyingState = false;
        }
    }

    private void RebuildScene(RobotPosition? robot = null)
    {
        var selected = Zones.Where(z => z.Selected).Select(z => z.Id).ToHashSet();
        Scene = new MapScene
        {
            Grid = _grid,
            Map = _map,
            ZoneMetadata = _maps.FirstOrDefault(m => m.Id == _map?.Id)?.Zones,
            Dock = _map?.DockLocation,
            Robot = robot ?? Scene.Robot,
            Path = SelectedClean is { } c ? _selectedCleanPath : Scene.Path,
            SelectedZoneIds = selected,
        };
    }

    private IReadOnlyList<MyDyson.Core.Point>? _selectedCleanPath;

    // ---- Loading -------------------------------------------------------------

    private async Task RefreshStateAsync()
    {
        if (_session is null || _session.Status != RobotConnectionStatus.Connected) return;
        try { await _session.RefreshStateAsync(); }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException) { AddLog($"état: {ex.Message}"); }
    }

    private async Task LoadMapsAsync()
    {
        try
        {
            _maps = await _ctx.Api.GetMapMetadataAsync(Serial);
            var current = _maps.FirstOrDefault(m => m.IsCurrentMap) ?? _maps.FirstOrDefault();
            if (current is null) return;
            CurrentMapName = current.Name ?? current.Id;
            Zones.Clear();
            foreach (var z in current.Zones ?? [])
            {
                var item = new ZoneItem(z);
                item.PropertyChanged += (_, _) => RebuildScene();
                Zones.Add(item);
            }
            await LoadMapGeometryAsync(current.Id);
        }
        catch (Exception ex) { AddLog($"cartes: {ex.Message}"); }
    }

    private async Task LoadMapGeometryAsync(string mapId)
    {
        try
        {
            var mapTask = _ctx.Api.GetPersistentMapAsync(Serial, mapId);
            var gridTask = _grid is null ? _ctx.Api.GetMappingMapAsync(Serial) : Task.FromResult<MappingMap?>(null)!;
            _map = await mapTask;
            var grid = await gridTask;
            if (grid is not null) _grid = MapGrid.From(grid);
            var live = await _ctx.Api.GetLiveCleaningMapAsync(Serial);
            _selectedCleanPath ??= live.CleanPath;
            RebuildScene(live.RobotLocation);
        }
        catch (Exception ex) { AddLog($"carte {mapId}: {ex.Message}"); }
    }

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
        if (value is null) { _selectedCleanPath = null; RebuildScene(); return; }
        _ = ShowCleanAsync(value);
    }

    private async Task ShowCleanAsync(CleanItem item)
    {
        try
        {
            var detail = await _ctx.Api.GetCleanDetailAsync(Serial, item.Summary.CleanId);
            _selectedCleanPath = detail.CleanPath;
            RebuildScene();
        }
        catch (Exception ex) { AddLog($"nettoyage: {ex.Message}"); }
    }

    // ---- Commands ------------------------------------------------------------

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
        var zones = Zones.Where(z => z.Selected).Select(z => z.Id).ToList();
        if (zones.Count == 0 || _map is null) { Message = "Sélectionnez au moins une pièce."; return Task.CompletedTask; }
        var mapId = long.Parse(_map.Id);
        return RunAsync($"démarrage de {zones.Count} pièce(s)", async c =>
        {
            // Same order as the app: preferences, START, current map, room list.
            var prefs = await c.RequestJdmAsync("service.get_preference", new System.Text.Json.Nodes.JsonObject { ["map_id"] = mapId });
            if (prefs["data"]?["room"] is System.Text.Json.Nodes.JsonArray rooms)
                await c.SetRoomPreferenceAsync(mapId, (System.Text.Json.Nodes.JsonArray)rooms.DeepClone(), prefs["data"]?["uv_switch"]?.DeepClone() as System.Text.Json.Nodes.JsonArray);
            await c.StartZoneCleanAsync(_map.Id, zones);
            await c.SetCurrentMapAsync(mapId);
            await c.SetRoomCleanAsync(zones.Select(int.Parse));
        });
    }

    [RelayCommand] private Task PauseAsync() => RunAsync("pause", c => c.PauseAsync());

    [RelayCommand]
    private Task AbortAsync() => RunAsync("retour à la station", async c =>
    {
        var st = _session!.Tracker.State;
        await c.AbortAsync(st?.State ?? "FULL_CLEAN_RUNNING", st?.CurrentCleaningMode ?? "zoneConfigured");
    });

    [RelayCommand] private Task StopDryingAsync() => RunAsync("arrêt du séchage", async c => { await c.AbortDockActionAsync("DRY_MOP"); await c.StartStationActionAsync(0, 2); });
    [RelayCommand] private Task CollectDustAsync() => RunAsync("vidage du bac", c => c.CollectDustAsync());
    [RelayCommand] private Task RefreshAsync() => Task.WhenAll(RefreshStateAsync(), LoadHistoryAsync());

    [RelayCommand]
    private void ExportMap()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog { Filter = "Image PNG|*.png", FileName = $"carte-{CurrentMapName}.png" };
        if (dlg.ShowDialog() == true)
        {
            MapRenderer.ExportPng(Scene, 1200, 1400, dlg.FileName);
            AddLog($"carte exportée vers {dlg.FileName}");
        }
    }

    // Settings: only react to user changes, not to values coming from the robot.
    partial void OnHotWaterMopChanged(bool value) { if (!_applyingState) _ = RunAsync("eau chaude serpillière", c => c.SetHotWaterMopAsync(value)); }
    partial void OnHotWaterSwitchChanged(bool value) { if (!_applyingState) _ = RunAsync("chauffe-eau", c => c.SetHotWaterSwitchAsync(value)); }
    partial void OnDetergentChanged(bool value) { if (!_applyingState) _ = RunAsync("détergent", c => c.SetDetergentAsync(value)); }
    partial void OnAlarmChanged(bool value) { if (!_applyingState) _ = RunAsync("sons", c => c.SetAlarmAsync(value)); }
    partial void OnAirDryFrequencyChanged(int value) { if (!_applyingState) _ = RunAsync("séchage", c => c.SetAirDryFrequencyAsync(value)); }

    [RelayCommand] private Task ApplyVolumeAsync() => RunAsync($"volume {Volume}", c => c.SetVolumeAsync(Volume));
    [RelayCommand] private Task BackWashPerRoomAsync() => RunAsync("rinçage par pièce", c => c.SetBackWashPerRoomAsync());
    [RelayCommand] private Task BackWashEvery15Async() => RunAsync("rinçage 15 min", c => c.SetBackWashByTimeAsync(15));
    [RelayCommand] private Task BackWashEvery30Async() => RunAsync("rinçage 30 min", c => c.SetBackWashByTimeAsync(30));

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
        "WASHING_MOP" => "lavage de la serpillière",
        "DRYING_MOP" => "séchage de la serpillière",
        "COLLECTING_DUST" => "vidage du bac",
        var d => d,
    };

    private static string DescribeConsumable(string type) => type switch
    {
        "brushBar" => "Brosse principale",
        "mopRoller" => "Rouleau serpillière",
        "sideBrushes" => "Brosses latérales",
        "robotFilter" => "Filtre du robot",
        "dockFilter" => "Filtre de la station",
        "ioniserCartridge" => "Cartouche ioniseur",
        "cleaningSolution" => "Solution de nettoyage",
        var t => t,
    };

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";

    public async Task ShutdownAsync()
    {
        _refresh.Stop();
        await _ctx.DisposeAsync();
    }
}
