using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using DyssCockpit.Presentation.Map;
using DyssCockpit.Presentation.Services;
using DyssCockpit.Core;

using static DyssCockpit.Core.Translation;

namespace DyssCockpit.Presentation.ViewModels;

/// <summary>
/// One row of the history list. A class rather than a record: MapName and Rooms are filled in after
/// construction (see HistoryViewModel.FillDetailsAsync), since the accurate source for "which
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
    /// launch showed isSelected true minutes later, once its selection changed again).
    /// zones[].cleanStatus, only present in the per-clean detail, is the one field confirmed to
    /// reflect this specific run (CLEAN_NOT_REQUESTED for a room not part of it).
    /// </summary>
    [ObservableProperty] private string _rooms = "…";
}

/// <summary>The Historique tab: the list of past cleans and the selected one's trail on its map.</summary>
public sealed partial class HistoryViewModel : ObservableObject, IDisposable
{
    private readonly RobotHub _hub;
    private readonly MapCatalog _maps;
    private readonly AppSettings _settings;
    private readonly CleanArchive _archive;
    private readonly Dictionary<string, Task<CleanDetail>> _details = new();
    private readonly Dictionary<string, Task<MapGrid?>> _snapshots = new();

    /// <param name="archive">Where the cleans are kept when the setting asks for it; null for the user's own folder.</param>
    public HistoryViewModel(RobotHub hub, MapCatalog maps, AppSettings settings, CleanArchive? archive = null)
    {
        _hub = hub;
        _maps = maps;
        _settings = settings;
        _archive = archive ?? new CleanArchive(AppFolders.Cleans(SessionStore.Directory));
        _settings.PropertyChanged += OnSettingChanged;
        Canvas.ShowPointerCoordinates = settings.ShowPointerCoordinates;
    }

    /// <summary>
    /// Switching the copy on keeps what the list shows at once, rather than at the next start; a
    /// shorter retention clears what is now too old (the page has asked first).
    /// </summary>
    private void OnSettingChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AppSettings.ArchiveCleans) when _settings.ArchiveCleans && History.Count > 0:
                _details.Clear();   // the details downloaded so far were not kept: fetched again, and kept this time
                _ = FillDetailsAsync();
                break;
            case nameof(AppSettings.CleanArchiveRetentionDays) when !_settings.LeavesFilesAlone:
                _archive.Purge(_settings.CleanArchiveRetentionDays);
                break;
        }
    }

    public void Dispose() => _settings.PropertyChanged -= OnSettingChanged;

    /// <summary>Whether what comes from the cloud is kept: what the setting asks, unless this run leaves the user's files alone (a screenshot). Reading what is kept only needs the setting.</summary>
    private bool Keeping => _settings.ArchiveCleans && !_settings.LeavesFilesAlone;

    public ObservableCollection<CleanItem> History { get; } = new();
    [ObservableProperty] private CleanItem? _selectedClean;
    [ObservableProperty] private MapScene _scene = new();
    [ObservableProperty] private string _mapName = "";

    /// <summary>The selected clean's map: zoom and pan only, clicks on it mean nothing here.</summary>
    public MapInteraction Canvas { get; } = new();

    partial void OnSceneChanged(MapScene value) => Canvas.Scene = value;
    [ObservableProperty] private string _resultText = "";

    private IReadOnlyList<DyssCockpit.Core.Point>? _path;
    private IReadOnlyList<DyssCockpit.Core.Point>? _obstacles;
    private IReadOnlyList<DirtSpot>? _dirt;
    /// <summary>The rectangles of the selected clean when it was a zone clean, drawn as they were when it started.</summary>
    private IReadOnlyList<IReadOnlyList<DyssCockpit.Core.Point>>? _spots;
    /// <summary>The map the selected clean ran on, from the cloud's snapshot of it; null when there is none to be had.</summary>
    private MapGrid? _grid;
    private PersistentMap? _map;

    public async Task LoadAsync()
    {
        try
        {
            var cloud = await _hub.Api.GetCleanHistoryAsync(_hub.Serial, _hub.Ct);
            History.Clear();
            foreach (var c in Merge(cloud)) History.Add(new CleanItem(c));
        }
        catch (OperationCanceledException) when (_hub.IsShuttingDown) { }
        catch (Exception ex)
        {
            _hub.AddLog(T($"historique: {ex.Message}", $"history: {ex.Message}"));
            // Without the cloud (offline), what was kept is still there to look at.
            if (_settings.ArchiveCleans && History.Count == 0)
                foreach (var c in _archive.Summaries()) History.Add(new CleanItem(c));
        }
    }

    /// <summary>
    /// The cloud's cleans, newest first, then the kept ones it no longer lists, in their place by date.
    /// The cloud's line wins for a clean both have. Only when keeping is on: off, the list is the cloud's.
    /// </summary>
    private IEnumerable<CleanSummary> Merge(IReadOnlyList<CleanSummary> cloud)
    {
        if (!_settings.ArchiveCleans) return cloud;
        var known = cloud.Select(c => c.CleanId).ToHashSet(StringComparer.Ordinal);
        return cloud.Concat(_archive.Summaries().Where(s => !known.Contains(s.CleanId))).OrderByDescending(c => c.StartTime ?? 0);
    }


    /// <summary>Waits before each look for the clean the robot just reported: the cloud files it shortly after the report.</summary>
    public IReadOnlyList<TimeSpan> RefreshDelays { get; init; } = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2)];

    /// <summary>
    /// After the robot has reported a clean (event.clean_record): reloads the list until a clean newer than the ones shown appears,
    /// keeping the row the user had chosen, then fills in the details.
    /// </summary>
    public async Task RefreshAfterCleanAsync()
    {
        var newest = History.FirstOrDefault()?.Summary.CleanId;
        foreach (var delay in RefreshDelays)
        {
            try { await Task.Delay(delay, _hub.Ct); }
            catch (OperationCanceledException) { return; }
            var selected = SelectedClean?.Summary.CleanId;
            await LoadAsync();
            if (selected is not null) SelectedClean = History.FirstOrDefault(c => c.Summary.CleanId == selected);
            if (History.FirstOrDefault()?.Summary.CleanId is { } id && id != newest)
            {
                await FillDetailsAsync();
                return;
            }
        }
    }
    /// <summary>
    /// Resolves each row's map name (cheap, from the already-loaded maps) and, one at a time so as
    /// not to fetch several hundred KB per entry all at once, its actual room list from that
    /// clean's own detail. Called after the maps and the list have both loaded.
    /// </summary>
    public async Task FillDetailsAsync()
    {
        foreach (var item in History)
            item.MapName = _maps.NameOf(item.Summary.PersistentMapId) ?? item.Summary.PersistentMapId ?? "";

        foreach (var item in History)
        {
            try
            {
                var detail = await GetDetailAsync(item.Summary);
                item.Rooms = DescribeRooms(item.Summary, detail);
                if (Keeping) await CompleteKeptSnapshotAsync(item.Summary);
            }
            catch (OperationCanceledException) when (_hub.IsShuttingDown) { return; }
            catch (Exception ex) { item.Rooms = ""; _hub.AddLog(T($"pièces du nettoyage {item.Summary.CleanId}: {ex.Message}", $"rooms of clean {item.Summary.CleanId}: {ex.Message}")); }
        }
    }

    /// <summary>
    /// Caches the download itself, not just its result: clicking a row while the background fill
    /// is already fetching that same clean must join that download (several hundred KB), not start
    /// a second one. A failed download is forgotten so the next request retries.
    /// </summary>
    private async Task<CleanDetail> GetDetailAsync(CleanSummary summary)
    {
        var cleanId = summary.CleanId;
        if (!_details.TryGetValue(cleanId, out var pending))
            _details[cleanId] = pending = DownloadOrReadAsync(summary);
        try
        {
            return await pending;
        }
        catch
        {
            if (ReferenceEquals(_details.GetValueOrDefault(cleanId), pending)) _details.Remove(cleanId);
            throw;
        }
    }

    /// <summary>
    /// With keeping on, a clean already kept is read from disk, which is quicker and works offline;
    /// otherwise it comes from the cloud and, with keeping on, is kept on its way (what the cloud no
    /// longer lists can only be read from disk). The disk work is off the UI thread.
    /// </summary>
    private async Task<CleanDetail> DownloadOrReadAsync(CleanSummary summary)
    {
        if (_settings.ArchiveCleans && await Task.Run(() => _archive.LoadDetail(summary.CleanId)) is { } kept) return kept;
        var detail = await _hub.Api.GetCleanDetailAsync(_hub.Serial, summary.CleanId, _hub.Ct);
        var snapshot = Keeping ? await DownloadSnapshotAsync(summary) : null;
        if (Keeping && !await Task.Run(() => _archive.Save(summary, detail, snapshot)))
            _hub.AddLog(T($"nettoyage {summary.CleanId} : copie locale impossible", $"clean {summary.CleanId}: local copy failed"));
        return detail;
    }

    /// <summary>
    /// The grid of the map as it was at the end of a clean (see <see cref="CleanSnapshot"/>): from the
    /// kept copy when there is one, from the cloud otherwise. Shared like the detail, and null rather
    /// than an error when it cannot be had: the clean is then shown on the current map, as before.
    /// </summary>
    private Task<MapGrid?> GetSnapshotAsync(CleanSummary summary)
    {
        if (!_snapshots.TryGetValue(summary.CleanId, out var pending))
            _snapshots[summary.CleanId] = pending = ReadOrDownloadSnapshotAsync(summary);
        return pending;
    }

    private async Task<MapGrid?> ReadOrDownloadSnapshotAsync(CleanSummary summary)
    {
        var blob = _settings.ArchiveCleans ? await Task.Run(() => _archive.LoadSnapshot(summary.CleanId)) : null;
        if (blob is null && (blob = await DownloadSnapshotAsync(summary)) is not null && Keeping)
            await Task.Run(() => _archive.AddSnapshot(summary.CleanId, blob));
        return blob is null ? null : CleanSnapshot.Parse(blob)?.Grid;
    }

    /// <summary>
    /// A clean kept without its snapshot — kept before snapshots were, or while its link failed —
    /// gets it now, while the cloud still lists the clean: once forgotten there, its map is lost.
    /// </summary>
    private async Task CompleteKeptSnapshotAsync(CleanSummary summary)
    {
        if (summary.DownloadUrl is not { Length: > 0 }) return;
        if (!await Task.Run(() => _archive.Has(summary.CleanId) && _archive.LoadSnapshot(summary.CleanId) is null)) return;
        if (await DownloadSnapshotAsync(summary) is { } blob) await Task.Run(() => _archive.AddSnapshot(summary.CleanId, blob));
    }

    /// <summary>
    /// The snapshot's blob from the cloud; null when the clean has none (a kept one the cloud has
    /// forgotten) or the download fails. Its link lasts 15 minutes after the list was read: an
    /// expired one is replaced by the link of a fresh read of the list, once.
    /// </summary>
    private async Task<byte[]?> DownloadSnapshotAsync(CleanSummary summary)
    {
        if (summary.DownloadUrl is not { Length: > 0 } url) return null;
        try
        {
            return await _hub.Api.GetCleanSnapshotAsync(url, _hub.Ct);
        }
        catch (DysonApiException) when (!_hub.IsShuttingDown)
        {
            try
            {
                var fresh = (await _hub.Api.GetCleanHistoryAsync(_hub.Serial, _hub.Ct)).FirstOrDefault(c => c.CleanId == summary.CleanId)?.DownloadUrl;
                return fresh is { Length: > 0 } && fresh != url ? await _hub.Api.GetCleanSnapshotAsync(fresh, _hub.Ct) : null;
            }
            catch (Exception ex) when (ex is DysonApiException or HttpRequestException)
            {
                _hub.AddLog(T($"carte du nettoyage {summary.CleanId} : {ex.Message}", $"map of clean {summary.CleanId}: {ex.Message}"));
                return null;
            }
        }
        catch (HttpRequestException ex)
        {
            _hub.AddLog(T($"carte du nettoyage {summary.CleanId} : {ex.Message}", $"map of clean {summary.CleanId}: {ex.Message}"));
            return null;
        }
    }

    /// <summary>
    /// The rooms actually part of a clean. zones[].isSelected mirrors the map's *current* room
    /// preference rather than what was picked for this specific task (confirmed against a capture:
    /// a room excluded at launch still showed isSelected true once its selection later changed), so
    /// cleanStatus is used instead — CLEAN_NOT_REQUESTED is the one value confirmed to reflect this
    /// particular run rather than the map's present state. A zone clean has no room of its own: its
    /// detail still marks the rooms of the robot's last room task (a room CLEAN_PENDING), so it is
    /// told by its rectangles instead.
    /// </summary>
    internal static string DescribeRooms(CleanSummary summary, CleanDetail detail) =>
        summary.IsSpotClean == true
            ? DescribeSpotZones(summary.SpotZones)
            : string.Join(", ", (detail.Zones ?? [])
                .Where(z => z.CleanStatus is not (null or "CLEAN_NOT_REQUESTED"))
                .Select(z => RoomTypeLabels.Resolve(z.Type, z.Name, z.Id)));

    /// <summary>"Zone 2,50 × 1,50 m", or "2 zones : 3,00 × 0,90 m, 0,75 × 2,85 m": width along x, then height along y.</summary>
    internal static string DescribeSpotZones(IReadOnlyList<SpotZone>? zones) =>
        MapGeometry.ZonesText((zones ?? []).Select(z => (IReadOnlyList<DyssCockpit.Core.Point>?)z.Points ?? []));

    partial void OnSelectedCleanChanged(CleanItem? value)
    {
        if (value is null) { Scene = new MapScene(); MapName = ""; ResultText = ""; return; }
        Showing = ShowCleanAsync(value);
    }

    /// <summary>The showing of the clean last selected, done or under way: what a test waits for, rather than looking again and again.</summary>
    internal Task Showing { get; private set; } = Task.CompletedTask;

    private async Task ShowCleanAsync(CleanItem item)
    {
        try
        {
            var detail = await GetDetailAsync(item.Summary);
            _grid = await GetSnapshotAsync(item.Summary);
            _path = detail.CleanPath;
            _obstacles = detail.Obstacles;
            _dirt = detail.Dirt;
            _spots = item.Summary.IsSpotClean == true ? SpotZone.Rectangles(item.Summary.SpotZones) : null;
            // The REST clean list has no overall success/failure field (see docs/protocole.md); the
            // closest thing is each zone's own status from this per-clean detail call. Only surface
            // zones that didn't simply complete, so an ordinary clean just reads T("Terminé", "Done").
            var problems = detail.Zones?
                .Where(z => z.CleanStatus is not (null or "CLEAN_NOT_REQUESTED" or "CLEAN_COMPLETE"))
                .Select(z => $"{RoomTypeLabels.Resolve(z.Type, z.Name, z.Id)} : {CleanStatusLabels.Resolve(z.CleanStatus)}")
                .ToList() ?? [];
            ResultText = problems.Count > 0 ? string.Join(", ", problems) : T("Terminé", "Done");
            item.Rooms = DescribeRooms(item.Summary, detail);
            var mapId = detail.PersistentMapId ?? item.Summary.PersistentMapId;
            _map = null;
            if (mapId is not null)
            {
                try { _map = await _maps.GetMapAsync(mapId); }
                catch (Exception ex) when (!_hub.IsShuttingDown) { _hub.AddLog(T($"carte {mapId} du nettoyage: {ex.Message}", $"map {mapId} of the clean: {ex.Message}")); }
            }
            // Reads inside "Trajet du nettoyage sélectionné sur la carte …". A clean can name a map
            // the account no longer has, deleted or replaced since; saying so beats printing a raw
            // id at the user, which is what the missing-map case used to do.
            MapName = _maps.NameOf(mapId) ?? (mapId is null ? T("inconnue", "unknown") : T("supprimée", "deleted"));
            RebuildScene();
        }
        catch (OperationCanceledException) when (_hub.IsShuttingDown) { }
        catch (Exception ex) { _hub.AddLog(T($"nettoyage: {ex.Message}", $"clean: {ex.Message}")); }
    }

    public void RebuildScene()
    {
        // The preference may just have changed: the scene is rebuilt on every change of preferences.
        Canvas.ShowPointerCoordinates = _settings.ShowPointerCoordinates;
        var mapId = _map?.Id;
        Scene = new MapScene
        {
            // The map as the clean left it; the current one when the cloud had no snapshot of it.
            Grid = _grid ?? _maps.GridFor(mapId),
            Map = _map,
            ZoneMetadata = _maps.Find(mapId)?.Metadata.Zones,
            Dock = _map?.DockLocation,
            Path = _path,
            Obstacles = _obstacles,
            DirtSpots = _dirt,
            SpotZones = _spots is { Count: > 0 } ? _spots : null,
            ShowFurniture = _settings.ShowFurniture,
            ShowTravelPath = _settings.ShowTravelPath,
            ShowCleanedArea = _settings.ShowCleanedArea,
        };
    }

    /// <summary>For screenshots: selects the most recent clean.</summary>
    public void SelectFirstClean() => SelectedClean = History.FirstOrDefault();
}
