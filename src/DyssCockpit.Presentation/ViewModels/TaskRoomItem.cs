using DyssCockpit.Core;

using static DyssCockpit.Core.Translation;

namespace DyssCockpit.Presentation.ViewModels;

/// <summary>Where a room stands in the clean under way, as the cloud's live map says it (<c>cleanStatus</c>).</summary>
public enum TaskRoomStatus
{
    /// <summary>Done, in the order they are told: what has been cleaned, then what is being cleaned, then what is to come.</summary>
    Complete,
    InProgress,
    Pending,
    /// <summary>The robot could not get there.</summary>
    Unreachable,
}

/// <summary>One room of the clean under way, for the dashboard's plan of it.</summary>
public sealed record TaskRoomItem(string Id, string Name, TaskRoomStatus Status)
{
    public string StatusText => Status switch
    {
        TaskRoomStatus.Complete => T("Task_Complete"),
        TaskRoomStatus.InProgress => T("Task_InProgress"),
        TaskRoomStatus.Unreachable => T("Task_Unreachable"),
        _ => T("Task_Pending"),
    };

    /// <summary>A Segoe Fluent glyph: a check, a play, a ring, a warning.</summary>
    public string Glyph => Status switch
    {
        TaskRoomStatus.Complete => "",
        TaskRoomStatus.InProgress => "",
        TaskRoomStatus.Unreachable => "",
        _ => "",
    };

    public bool IsComplete => Status == TaskRoomStatus.Complete;
    public bool IsCurrent => Status == TaskRoomStatus.InProgress;
    public bool IsUnreachable => Status == TaskRoomStatus.Unreachable;
    public bool IsPending => Status == TaskRoomStatus.Pending;

    /// <summary>The room of a live map zone, or null when the task has nothing to do with it (not asked for, or a status this version does not know).</summary>
    public static TaskRoomItem? From(MapZone zone) => zone.CleanStatus switch
    {
        "CLEAN_COMPLETE" => new(zone.Id, Label(zone), TaskRoomStatus.Complete),
        "CLEAN_IN_PROGRESS" => new(zone.Id, Label(zone), TaskRoomStatus.InProgress),
        "CLEAN_PENDING" => new(zone.Id, Label(zone), TaskRoomStatus.Pending),
        "CANT_CLEAN" => new(zone.Id, Label(zone), TaskRoomStatus.Unreachable),
        _ => null,
    };

    private static string Label(MapZone zone) => RoomTypeLabels.Resolve(zone.Type, zone.Name, zone.Id);
}
