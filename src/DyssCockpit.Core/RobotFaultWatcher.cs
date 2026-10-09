namespace DyssCockpit.Core;

/// <summary>
/// Tells when the robot starts to report a fault that needs someone: from each state it gets, the
/// faults that were not in the one before. Pure, like <see cref="CleaningTaskDetector"/>.
/// <para>
/// The faults are the <c>activeFaults</c> of the robot's state that are not status indicators (the
/// 21xx family with <c>LOG_ONLY</c>, which says what the robot is doing, not what is wrong): in the
/// captures, 589 (it cannot locate itself), and 2007 (a room it cannot reach, which stays until it
/// is back on the dock). A fault is told once, however many states repeat it; if it goes and comes
/// back, it is told again. Every state of the captures carries <c>activeFaults</c>, empty when
/// there is none, so a message that carries no state (a position alone) is left out rather than read as
/// "no fault".
/// </para>
/// <para>
/// 501 is not told: it is a passing fault, raised on the way back to the dock after the robot has
/// given up a clean, which the fault that caused that (589) has already told.
/// </para>
/// </summary>
public sealed class RobotFaultWatcher
{
    /// <summary>Faults that come and go with another one that is told: see the class summary.</summary>
    private static readonly HashSet<string> Passing = ["501"];

    private HashSet<string>? _known;

    /// <summary>
    /// Forgets what was seen, so that the next state is only a starting point: called when the
    /// connection drops. A fault already there when the application starts, or back from a lost
    /// connection, is on the window and the icon's badge, not news.
    /// </summary>
    public void Reset() => _known = null;

    /// <summary>The faults of this state that the previous one did not have; none for the first state after <see cref="Reset"/>.</summary>
    public IReadOnlyList<ActiveFault> OnState(RobotState state)
    {
        if (state.State is null) return [];
        var faults = state.RealFaults.Where(f => !Passing.Contains(f.FaultCode)).ToList();
        var before = _known;
        _known = [.. faults.Select(f => f.FaultCode)];
        return before is null ? [] : [.. faults.Where(f => !before.Contains(f.FaultCode))];
    }
}
