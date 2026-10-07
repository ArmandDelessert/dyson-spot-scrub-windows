using System.Text.Json.Nodes;

namespace DyssCockpit.Core;

/// <summary>What happened to the robot's cleaning task.</summary>
public enum CleaningTaskChange
{
    /// <summary>The robot set off to clean.</summary>
    Started,

    /// <summary>The robot cleaned what it was sent to, and is done, washing included.</summary>
    Finished,

    /// <summary>The robot gave up: it could not find its place on the map, or could not reach a room.</summary>
    Abandoned,
}

/// <param name="Change">What happened.</param>
/// <param name="CleaningMode">For a start: the robot's <c>currentCleaningMode</c>, <c>spotZoneConfigured</c> for a drawn zone.</param>
/// <param name="Minutes">For an end: how long the robot cleaned, when its report says.</param>
public sealed record CleaningTaskNotice(CleaningTaskChange Change, string? CleaningMode = null, int? Minutes = null)
{
    /// <summary>A zone drawn on the map, as opposed to rooms.</summary>
    public bool IsZone => CleaningMode == "spotZoneConfigured";
}

/// <summary>
/// Tells when a cleaning task starts and when it ends, from what the robot pushes: its state, and
/// its events. Pure, so it can be fed the sequences of the captures. Each change is reported once,
/// to whoever decides what to do about it (a notification, say).
/// <para>
/// <b>Start.</b> The state becomes one of the <c>FULL_CLEAN_*</c> running ones from a state that was
/// not: the robot waiting, charging or having just stopped. Not a start: the resume after a pause
/// (<c>FULL_CLEAN_PAUSED</c> then <c>FULL_CLEAN_RUNNING</c>), the relocalisation that follows a
/// start or a wash (<c>FULL_CLEAN_DISCOVERING</c>), and the trip to the dock to wash the roller
/// and back (the state stays <c>FULL_CLEAN_RUNNING</c> throughout, or goes through
/// <c>FULL_CLEAN_CHARGING</c> when it recharges), since each of those starts from a state that is
/// already cleaning. Not a start either: the mapping, whose states are <c>MAPPING_*</c>, and the
/// first state seen after <see cref="Reset"/> — that is the application starting, or coming back
/// from a lost connection, while the robot is already at work: whatever it was doing began before,
/// maybe long before.
/// </para>
/// <para>
/// <b>End.</b> <c>event.clean_finish.post</c> comes once the robot is back on its dock and has
/// washed its roller, minutes after <c>FULL_CLEAN_FINISHED</c>, which is when the phone's
/// notification comes too. It is followed within the second by <c>event.clean_record.post</c>, the
/// task's report, whose <c>record_task_status</c> tells how it went: 1 done, 2 stopped by the user,
/// 4 given up by the robot. The captures have the report in every case, but the finish only for a
/// clean that went to its end or was given up for an unreachable room: a robot that cannot locate
/// itself gives up without any <c>clean_finish</c>, and the finish of an unreachable room is
/// followed by status 4, so <c>clean_finish</c> alone does not mean the clean was done. So the
/// report decides: a done task is <see cref="CleaningTaskChange.Finished"/>, a task
/// given up is <see cref="CleaningTaskChange.Abandoned"/>, and one the user stopped is nothing, they
/// know. A mapping ends with a report too (status 1, mode 4, no <c>clean_finish</c>), which is
/// nothing either: a report is a finished clean only if the finish came before it, or, should that
/// message have been lost, if it is a rooms clean (mode 0), which a mapping never is.
/// </para>
/// </summary>
public sealed class CleaningTaskDetector
{
    private RobotState? _previous;
    private bool _finishSeen;
    private bool _mapping;
    private long? _lastReport;

    /// <summary>
    /// Forgets the state seen so far, so that the next one is only a starting point: called when the
    /// connection to the robot drops, since what the robot did meanwhile is not known to have just begun.
    /// </summary>
    public void Reset() => _previous = null;

    /// <summary>Applies the robot's state; returns what it means for the task, if anything.</summary>
    public CleaningTaskNotice? OnState(RobotState state)
    {
        // A message that carries no state (only a position, say) leaves the picture as it was.
        if (state.State is null) return null;
        var before = _previous;
        _previous = state;
        if (state.IsMapping) _mapping = true;
        if (before is null || before.IsCleaning || !state.IsCleaning) return null;

        _finishSeen = false;
        _mapping = false;
        return new CleaningTaskNotice(CleaningTaskChange.Started, state.CurrentCleaningMode);
    }

    /// <summary>Applies an event of the robot, the whole message as pushed; returns what it means for the task, if anything.</summary>
    public CleaningTaskNotice? OnEvent(string name, JsonObject message)
    {
        switch (name)
        {
            case "event.startBuildMap.post":
                _mapping = true;
                return null;
            case "event.clean_finish.post":
                _finishSeen = true;
                return null;
            case "event.clean_record.post":
                return OnReport(message["params"] as JsonObject);
            default:
                return null;
        }
    }

    private CleaningTaskNotice? OnReport(JsonObject? report)
    {
        var finishSeen = _finishSeen;
        var mapping = _mapping;
        _finishSeen = false;
        _mapping = false;

        // The same report twice, as a resend would be: one notification.
        var startedAt = IntegerOf(report?["record_start_time"]);
        if (startedAt is not null && startedAt == _lastReport) return null;
        _lastReport = startedAt ?? _lastReport;

        if (mapping) return null;
        return IntegerOf(report?["record_task_status"]) switch
        {
            1 when finishSeen || IntegerOf(report?["record_clean_mode"]) == 0 =>
                new CleaningTaskNotice(CleaningTaskChange.Finished, Minutes: (int?)IntegerOf(report?["record_use_time"])),
            4 => new CleaningTaskNotice(CleaningTaskChange.Abandoned),
            _ => null,
        };
    }

    private static long? IntegerOf(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<long>(out var integer) ? integer : null;
}
