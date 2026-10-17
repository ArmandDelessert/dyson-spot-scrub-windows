using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DyssCockpit.Presentation.Map;
using DyssCockpit.Core;

using static DyssCockpit.Core.Translation;

namespace DyssCockpit.Presentation.ViewModels;

/// <summary>The rooms tab: choosing a room, renaming it, merging rooms, cutting one in two.</summary>
public sealed partial class MapManagerViewModel
{
    // ---- Choosing rooms ----------------------------------------------------------

    /// <summary>A click on a room, from the list or the map. Chooses it, or gathers it for a merge.</summary>
    public void RoomClicked(ManagedRoom room)
    {
        if (Splitting || Busy || Layer != MapLayer.Rooms) return;
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

    /// <summary>Called by the map when a room is clicked on it.</summary>
    public void RoomClickedById(string zoneId)
    {
        if (Rooms.FirstOrDefault(r => r.Id == zoneId) is { } room) RoomClicked(room);
    }

    /// <summary>A click on empty map space: lets go of whatever is highlighted, in either mode.</summary>
    public void ClearRoomSelection()
    {
        if (Splitting || Layer != MapLayer.Rooms) return;
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

    // ---- Room commands -----------------------------------------------------------

    [RelayCommand(CanExecute = nameof(HasRoom))]
    private async Task RenameRoomAsync()
    {
        if (SelectedRoom is not { } room || SelectedMap is not { } map) return;
        if (await _hub.Dialogs.AskRoomNameAsync(room, RoomTypes) is not { } answer) return;
        if (string.IsNullOrWhiteSpace(answer.Name)) return;
        if (!TryMapId(map, out var mapId) || room.NumericId < 0) return;

        // A free name travels as type "custom", which is what the app sends too.
        await EditAsync(Tf($"pièce renommée en « {answer.Name.Trim()} »", $"room renamed to “{answer.Name.Trim()}”"),
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
            Hint = T("Cliquez les pièces à fusionner, sur la carte ou dans la liste. « Fusionner » à nouveau pour valider, Échap pour annuler.", "Click the rooms to merge, on the map or in the list. “Merge” again to confirm, Esc to cancel.");
            UpdateMergeLabel();
            RebuildScene();
            return;
        }

        if (SelectedMap is not { } map || !TryMapId(map, out var mapId)) return;
        var rooms = _mergeSet.Where(r => r.NumericId >= 0).ToList();
        if (rooms.Count < 2) { Status = T("Choisissez au moins deux pièces à fusionner.", "Choose at least two rooms to merge."); return; }
        if (!await _hub.Dialogs.ConfirmAsync(T("Fusionner les pièces", "Merge the rooms"),
                Tf($"Fusionner {string.Join(", ", rooms.Select(p => p.DisplayName))} en une seule pièce ?", $"Merge {string.Join(", ", rooms.Select(p => p.DisplayName))} into a single room?"))) return;

        LeaveModes();
        await EditAsync(Tf($"{rooms.Count} pièces fusionnées", $"{rooms.Count} rooms merged"), c => c.MergeRoomsAsync(mapId, rooms.Select(p => p.NumericId), MapLanguage.FromCulture(_hub.Culture), _hub.Ct),
            refused: T("Le robot a refusé la fusion : les pièces doivent se toucher.", "The robot refused the merge: the rooms must touch."));
    }

    private void UpdateMergeLabel()
    {
        MergeButtonLabel = !Merging ? T("Fusionner des pièces…", "Merge rooms…")
            : _mergeSet.Count < 2 ? T("Fusionner (choisissez 2 pièces ou plus)", "Merge (choose 2 rooms or more)")
            : Tf($"Fusionner les {_mergeSet.Count} pièces", $"Merge the {_mergeSet.Count} rooms");
        MergeCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(HasRoom))]
    private void StartSplit()
    {
        if (SelectedRoom is null) { Status = T("Choisissez d'abord la pièce à diviser.", "First choose the room to split."); return; }
        Splitting = true;
        Hint = T("Cliquez les deux extrémités du trait de coupe ; il se cale sur la grille de 5 cm du robot, visible en zoomant. Échap pour annuler.", "Click both ends of the cutting line; it snaps to the robot's 5 cm grid, visible when zoomed in. Esc to cancel.");
        Status = "";
        RefreshCommandStates();
    }

    /// <summary>Called by the map once both ends of the cut have been clicked.</summary>
    public async Task SplitAsync(DyssCockpit.Core.Point from, DyssCockpit.Core.Point to)
    {
        var room = SelectedRoom;
        LeaveModes();
        if (room is null || SelectedMap is not { } map) return;
        if (!TryMapId(map, out var mapId) || room.NumericId < 0) return;

        // A cut that misses the room, or that the robot will not make, comes back refused.
        await EditAsync(Tf($"pièce {room.DisplayName} divisée", $"room {room.DisplayName} split"), c => c.SplitRoomAsync(mapId, room.NumericId, from, to, MapLanguage.FromCulture(_hub.Culture), _hub.Ct),
            refused: T("Le robot a refusé la division : la pièce est sans doute trop petite à cet endroit, ou le trait ne la traverse pas.", "The robot refused the split: the room is probably too small there, or the line does not cross it."));
    }
}
