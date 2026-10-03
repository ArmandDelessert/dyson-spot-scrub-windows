using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Dyss.Presentation.Map;
using Dyss.Presentation.Services;
using Dyss.Core;

namespace Dyss.Presentation.ViewModels;

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
public sealed partial class HistoryViewModel(RobotHub hub, MapCatalog maps, DisplaySettings display) : ObservableObject
{
    private readonly Dictionary<string, Task<CleanDetail>> _details = new();

    public ObservableCollection<CleanItem> History { get; } = new();
    [ObservableProperty] private CleanItem? _selectedClean;
    [ObservableProperty] private MapScene _scene = new();
    [ObservableProperty] private string _mapName = "";

    /// <summary>The selected clean's map: zoom and pan only, clicks on it mean nothing here.</summary>
    public MapInteraction Canvas { get; } = new();

    partial void OnSceneChanged(MapScene value) => Canvas.Scene = value;
    [ObservableProperty] private string _resultText = "";

    private IReadOnlyList<Dyss.Core.Point>? _path;
    private IReadOnlyList<Dyss.Core.Point>? _obstacles;
    private IReadOnlyList<DirtSpot>? _dirt;
    private PersistentMap? _map;

    public async Task LoadAsync()
    {
        try
        {
            History.Clear();
            foreach (var c in await hub.Api.GetCleanHistoryAsync(hub.Serial, hub.Ct))
                History.Add(new CleanItem(c));
        }
        catch (OperationCanceledException) when (hub.IsShuttingDown) { }
        catch (Exception ex) { hub.AddLog($"historique: {ex.Message}"); }
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
            try { await Task.Delay(delay, hub.Ct); }
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
            item.MapName = maps.NameOf(item.Summary.PersistentMapId) ?? item.Summary.PersistentMapId ?? "";

        foreach (var item in History)
        {
            try
            {
                var detail = await GetDetailAsync(item.Summary.CleanId);
                item.Rooms = DescribeRooms(detail);
            }
            catch (OperationCanceledException) when (hub.IsShuttingDown) { return; }
            catch (Exception ex) { item.Rooms = ""; hub.AddLog($"pièces du nettoyage {item.Summary.CleanId}: {ex.Message}"); }
        }
    }

    /// <summary>
    /// Caches the download itself, not just its result: clicking a row while the background fill
    /// is already fetching that same clean must join that download (several hundred KB), not start
    /// a second one. A failed download is forgotten so the next request retries.
    /// </summary>
    private async Task<CleanDetail> GetDetailAsync(string cleanId)
    {
        if (!_details.TryGetValue(cleanId, out var pending))
            _details[cleanId] = pending = hub.Api.GetCleanDetailAsync(hub.Serial, cleanId, hub.Ct);
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
        if (value is null) { Scene = new MapScene(); MapName = ""; ResultText = ""; return; }
        _ = ShowCleanAsync(value);
    }

    private async Task ShowCleanAsync(CleanItem item)
    {
        try
        {
            var detail = await GetDetailAsync(item.Summary.CleanId);
            _path = detail.CleanPath;
            _obstacles = detail.Obstacles;
            _dirt = detail.Dirt;
            // The REST clean list has no overall success/failure field (see docs/protocole.md); the
            // closest thing is each zone's own status from this per-clean detail call. Only surface
            // zones that didn't simply complete, so an ordinary clean just reads "Terminé".
            var problems = detail.Zones?
                .Where(z => z.CleanStatus is not (null or "CLEAN_NOT_REQUESTED" or "CLEAN_COMPLETE"))
                .Select(z => $"{RoomTypeLabels.Resolve(z.Type, z.Name, z.Id)} : {CleanStatusLabels.Resolve(z.CleanStatus)}")
                .ToList() ?? [];
            ResultText = problems.Count > 0 ? string.Join(", ", problems) : "Terminé";
            item.Rooms = DescribeRooms(detail);
            var mapId = detail.PersistentMapId ?? item.Summary.PersistentMapId;
            _map = null;
            if (mapId is not null)
            {
                try { _map = await maps.GetMapAsync(mapId); }
                catch (Exception ex) when (!hub.IsShuttingDown) { hub.AddLog($"carte {mapId} du nettoyage: {ex.Message}"); }
            }
            // Reads inside "Trajet du nettoyage sélectionné sur la carte …". A clean can name a map
            // the account no longer has, deleted or replaced since; saying so beats printing a raw
            // id at the user, which is what the missing-map case used to do.
            MapName = maps.NameOf(mapId) ?? (mapId is null ? "inconnue" : "supprimée");
            RebuildScene();
        }
        catch (OperationCanceledException) when (hub.IsShuttingDown) { }
        catch (Exception ex) { hub.AddLog($"nettoyage: {ex.Message}"); }
    }

    public void RebuildScene()
    {
        var mapId = _map?.Id;
        Scene = new MapScene
        {
            Grid = maps.GridFor(mapId),
            Map = _map,
            ZoneMetadata = maps.Find(mapId)?.Metadata.Zones,
            Dock = _map?.DockLocation,
            Path = _path,
            Obstacles = _obstacles,
            DirtSpots = _dirt,
            ShowFurniture = display.ShowFurniture,
            ShowTravelPath = display.ShowTravelPath,
        };
    }

    /// <summary>For screenshots: selects the most recent clean.</summary>
    public void SelectFirstClean() => SelectedClean = History.FirstOrDefault();
}
