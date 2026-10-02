using Dyss.App.Rendering;
using Dyss.Core;

namespace Dyss.App.ViewModels;

/// <summary>
/// Works out from the robot's messages what its icon and its dock's should show: whether it sits on
/// the dock, what it is doing on the floor, and what the dock is doing. Both dialects are read: the
/// jdm properties are the more timely (charge_state, station_act, back_to_wash), the classic state
/// fills in when they have not been received yet.
/// </summary>
public sealed class RobotActivityTracker
{
    private RobotState? _state;
    private JdmProperties? _jdm;
    private bool? _lastPointWorking;
    private bool _wasCleaning;
    /// <summary>Whether the robot has driven off the dock since the clean began: a wash before that is the preparation.</summary>
    private bool _leftDockSinceStart;

    public bool Docked { get; private set; }
    public RobotActivity Robot { get; private set; }
    public DockActivity Dock { get; private set; }
    /// <summary>
    /// The task is over: back on the dock, charging, with nothing left to empty or wash (the hours
    /// of drying do not count). The phone then takes the trail off the map; it is in the history.
    /// </summary>
    public bool TaskOver { get; private set; }

    public void Apply(RobotState state)
    {
        if (state.IsPositionOnly) return;
        _state = state;
        Update();
    }

    public void ApplyJdm(JdmProperties jdm)
    {
        _jdm = jdm;
        Update();
    }

    /// <summary>The live trail: its last point says whether the robot is cleaning (update 1) or only driving (0).</summary>
    public void ApplyTrail(IReadOnlyList<RobotPosition> trail)
    {
        _lastPointWorking = trail.Count > 0 && trail[^1].Update is { } u ? u != 0 : null;
        Update();
    }

    private void Update()
    {
        var s = _state;
        var stationAct = _jdm?.StationAct is { } a and not 0 ? a : (int?)null;
        var dockState = s?.DockState is { } d and not "IDLE" ? d : null;
        var cleaning = s?.IsCleaning == true;

        Docked = (_jdm?.ChargeState ?? s?.IsDocked ?? false) || stationAct is not null || dockState is not null;

        if (cleaning && !_wasCleaning) _leftDockSinceStart = false;
        if (cleaning && !Docked) _leftDockSinceStart = true;
        _wasCleaning = cleaning;

        Dock = (stationAct, dockState) switch
        {
            (5, _) or (null, "COLLECTING_DUST") => DockActivity.EmptyingBin,
            (1, _) or (null, "WASHING_MOP") => cleaning && !_leftDockSinceStart ? DockActivity.FillingWater : DockActivity.WashingRoller,
            (2, _) or (null, "DRYING_MOP") => DockActivity.DryingRoller,
            _ when Docked && s?.State is "INACTIVE_CHARGING" or "FULL_CLEAN_CHARGING" => DockActivity.Charging,
            _ => DockActivity.Idle,
        };

        TaskOver = Docked && s?.State is "INACTIVE_CHARGING" or "INACTIVE_CHARGED"
            && Dock is not (DockActivity.EmptyingBin or DockActivity.WashingRoller or DockActivity.FillingWater);

        Robot = Docked || s is null ? RobotActivity.Idle
            : s.IsPaused ? RobotActivity.Idle
            : _jdm?.BackToWash == true || s.IsMapping ? RobotActivity.Moving
            : s.State != "FULL_CLEAN_RUNNING" ? (cleaning ? RobotActivity.Moving : RobotActivity.Idle)
            : _lastPointWorking == false ? RobotActivity.Moving
            : s.FullCleanAction switch
            {
                "VACUUMING" => RobotActivity.Vacuuming,
                "MOPPING" => RobotActivity.Mopping,
                "VACUUMING_AND_MOPPING" => RobotActivity.VacuumingAndMopping,
                _ => RobotActivity.Moving,
            };
    }
}
