using System.Collections.ObjectModel;
using MyDyson.Core;

namespace MyDyson.App.ViewModels;

public sealed record MapItem(MapMetadata Metadata)
{
    public string Id => Metadata.Id;
    public string Name => (Metadata.Name ?? Metadata.Id) + (Metadata.IsCurrentMap ? "  (active)" : "");
}

/// <summary>
/// The account's maps, shared by the clean tab (which picks one) and the history (which names
/// them and draws past cleans on them): the metadata list, each map's downloaded geometry, and
/// the occupancy grid, which only ever exists for the active map.
/// </summary>
public sealed class MapCatalog(RobotHub hub)
{
    private readonly Dictionary<string, Task<PersistentMap>> _maps = new();

    public ObservableCollection<MapItem> Maps { get; } = new();

    /// <summary>Grid of the map <see cref="GridMapId"/>; null until the active map's geometry has been loaded once.</summary>
    public MapGrid? Grid { get; private set; }
    public string? GridMapId { get; private set; }

    /// <summary>
    /// Forgets the downloaded geometry and grid, so the next read fetches them again. Needed after
    /// an edit in the map manager: a merge or a split renumbers rooms and redraws the grid, and the
    /// cached copy would keep the dashboard showing the rooms as they were.
    /// </summary>
    public void Invalidate()
    {
        _maps.Clear();
        Grid = null;
        GridMapId = null;
    }

    public MapItem? Find(string? id) => id is null ? null : Maps.FirstOrDefault(m => m.Id == id);
    public string? NameOf(string? id) => Find(id)?.Metadata.Name;
    public bool IsCurrent(string? id) => Find(id)?.Metadata.IsCurrentMap == true;

    /// <summary>Reloads the metadata list. Callers pick a selection afterwards; the list is cleared first, which drops any bound selection.</summary>
    public async Task LoadAsync()
    {
        var maps = await hub.Api.GetMapMetadataAsync(hub.Serial, hub.Ct);
        Maps.Clear();
        foreach (var m in maps) Maps.Add(new MapItem(m));
    }

    /// <summary>
    /// Caches the download itself, not just its result, so the clean tab and the history asking
    /// for the same map at the same time share one request. A failed download is forgotten so
    /// the next request retries.
    /// </summary>
    public async Task<PersistentMap> GetMapAsync(string mapId)
    {
        if (!_maps.TryGetValue(mapId, out var pending))
            _maps[mapId] = pending = hub.Api.GetPersistentMapAsync(hub.Serial, mapId, hub.Ct);
        try
        {
            return await pending;
        }
        catch
        {
            if (ReferenceEquals(_maps.GetValueOrDefault(mapId), pending)) _maps.Remove(mapId);
            throw;
        }
    }

    /// <summary>Downloads the occupancy grid for the active map if it is not the one already held. Returns whether it was (re)loaded.</summary>
    public async Task<bool> LoadGridAsync(string mapId)
    {
        if (GridMapId == mapId) return false;
        Grid = MapGrid.From(await hub.Api.GetMappingMapAsync(hub.Serial, hub.Ct));
        GridMapId = mapId;
        return true;
    }

    /// <summary>The grid, but only for the map it belongs to.</summary>
    public MapGrid? GridFor(string? mapId) => mapId is not null && GridMapId == mapId ? Grid : null;
}
