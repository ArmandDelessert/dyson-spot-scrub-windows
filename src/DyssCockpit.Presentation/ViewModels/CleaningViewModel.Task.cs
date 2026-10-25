using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DyssCockpit.Core;
using DyssCockpit.Presentation.Map;

using static DyssCockpit.Core.Translation;

namespace DyssCockpit.Presentation.ViewModels;

/// <summary>
/// The plan of the clean under way: which rooms it is about and how far it is, from the cloud's live
/// map, whoever started it — this window, the phone, a schedule. The robot's own messages say
/// neither; the live map says, for each room, whether it is done, being cleaned, still to come or
/// out of reach, and for a zone clean what rectangles it was sent to. It does not say in which order
/// the rooms are taken, and nor does this.
/// </summary>
public sealed partial class CleaningViewModel
{
    /// <summary>The rooms of the clean under way, those done first, then the one being cleaned, then those to come.</summary>
    public ObservableCollection<TaskRoomItem> TaskRooms { get; } = new();

    /// <summary>A clean is under way, or only just over (the robot is still at its dock): there is a plan to show.</summary>
    [ObservableProperty] private bool _showTask;

    /// <summary>"1 of 4 rooms done", or the size of the zones being cleaned.</summary>
    [ObservableProperty] private string _taskSummary = "";

    /// <summary>The rooms of the live map, as last read; null before the first reading and between two tasks.</summary>
    private IReadOnlyList<MapZone>? _liveRooms;

    /// <summary>How often the live map is read again while the robot cleans: rooms change in minutes, not seconds.</summary>
    public TimeSpan LiveRefreshInterval { get; init; } = TimeSpan.FromSeconds(20);

    private bool _cleaningNow;
    private bool _liveLoopRunning;

    /// <summary>Reads the live map once and shows what it says of the task: its zones and its rooms.</summary>
    public async Task RefreshLiveAsync()
    {
        if (SelectedMap is not { } map || !_maps.IsCurrent(map.Id)) return;
        var live = await _hub.Api.GetLiveCleaningMapAsync(_hub.Serial, _hub.Ct);
        _liveSpots = SpotZone.Rectangles(live.SpotZones);
        _liveRooms = live.Zones;
        RebuildScene();
    }

    /// <summary>
    /// Reads the live map every <see cref="LiveRefreshInterval"/> for as long as the robot cleans, and once
    /// more after it stops, which is when the last room turns done. Started once: a robot that pauses
    /// and goes on keeps the same loop.
    /// </summary>
    private async Task LiveLoopAsync()
    {
        _liveLoopRunning = true;
        try
        {
            // A moment first: the robot has only just started, and its rooms are not yet marked.
            await Task.Delay(LiveZonesDelay, _hub.Time, _hub.Ct);
            await RefreshLiveSafelyAsync();
            using var timer = new PeriodicTimer(LiveRefreshInterval, _hub.Time);
            while (await timer.WaitForNextTickAsync(_hub.Ct))
            {
                await RefreshLiveSafelyAsync();
                if (!_cleaningNow) break;
            }
        }
        catch (OperationCanceledException) when (_hub.IsShuttingDown) { }
        finally { _liveLoopRunning = false; }
    }

    private async Task RefreshLiveSafelyAsync()
    {
        try { await RefreshLiveAsync(); }
        catch (Exception ex) when (ex is not OperationCanceledException) { _hub.AddLog(Tf("Cleaning_ZonesOfTheClean", ex.Message)); }
    }

    /// <summary>The robot's state has been applied: a clean has begun or ended, which is when the plan is read.</summary>
    private void FollowTask(RobotState s)
    {
        _cleaningNow = s.IsCleaning;
        if (s.IsCleaning && !_liveLoopRunning) _ = LiveLoopAsync();
    }

    /// <summary>The plan of the task, as the live map last gave it; nothing once the task is over, and nothing for a map the robot is not on.</summary>
    private void RebuildTask()
    {
        var onShow = SelectedMap?.Metadata.IsCurrentMap == true && !_activity.TaskOver;
        var rooms = onShow
            ? (_liveRooms ?? []).Select(TaskRoomItem.From).OfType<TaskRoomItem>()
                .OrderBy(r => r.Status).ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).ToList()
            : [];
        if (!rooms.SequenceEqual(TaskRooms))
        {
            TaskRooms.Clear();
            foreach (var room in rooms) TaskRooms.Add(room);
        }
        var spots = onShow && _liveSpots is { Count: > 0 } ? _liveSpots : null;
        ShowTask = rooms.Count > 0 || spots is not null;
        TaskSummary = spots is not null ? MapGeometry.ZonesText(spots)
            : rooms.Count == 0 ? ""
            : Tf("Cleaning_RoomsDone", rooms.Count(r => r.Status == TaskRoomStatus.Complete), rooms.Count);
    }
}
