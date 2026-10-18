using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DyssCockpit.Core;

using static DyssCockpit.Core.Translation;

namespace DyssCockpit.Presentation.ViewModels;

/// <summary>Consumable as shown by the app: percentage of life left, replace at 0.</summary>
public sealed record ConsumableItem(string Name, int? Usage, bool? NeedsRefill)
{
    public int Remaining => Usage is { } u ? Math.Clamp(100 - u, 0, 100) : 100;
    public string Display => Usage is { } ? $"{Remaining} %" : NeedsRefill == true ? T("Status_ToRefill") : T("Status_Ready");
}

/// <summary>
/// The robot right now: the state card (what it is doing, battery, faults, pause and abort), the
/// dock card and the consumables. Fed by <see cref="Apply"/> for the classic dialect and
/// <see cref="ApplyJdm"/> for the jdm one.
/// </summary>
public sealed partial class StatusViewModel(RobotHub hub) : ObservableObject
{
    [ObservableProperty] private string _stateText = "";
    [ObservableProperty] private string _actionText = "";
    [ObservableProperty] private string _dockText = "";
    /// <summary>jdm back_to_wash: the robot has left the clean to go and wash its roller (the classic dialect only reports the washing once docked).</summary>
    [ObservableProperty] private bool _returningToWash;
    [ObservableProperty] private int _battery;
    [ObservableProperty] private string _faultText = "";
    [ObservableProperty] private bool _hasRealFault;
    [ObservableProperty] private string _lastUpdate = "";

    [ObservableProperty] private bool _canPause;
    [ObservableProperty] private bool _canAbort;
    [ObservableProperty] private bool _dockBusy;
    [ObservableProperty] private string _washDryLabel = T("Status_WashAndDry2");
    [ObservableProperty] private string _pauseResumeLabel = T("Status_Pause2");
    /// <summary>The clean is paused: the pause button resumes it, and shows it with its icon.</summary>
    [ObservableProperty] private bool _paused;

    public ObservableCollection<ConsumableItem> Consumables { get; } = new();

    private string? _dockState;
    private TimeSpan? _dryingRemaining;

    /// <summary>A fault in a few words: its code, and what it means when that is known (589, a locate failure; 2007, a room the robot cannot reach; 501, a wheel off the floor).</summary>
    public static string Describe(ActiveFault fault) =>
        Tf("Status_Fault", fault.FaultCode) + fault.FaultCode switch
        {
            "589" => (" " + T("Status_CannotLocateItself")),
            "2007" => (" " + T("Status_RoomUnreachable")),
            "501" => (" " + T("Status_WheelOffTheFloor")),
            _ => "",
        };

    /// <summary>The robot's state as last applied: pause, resume and abort tell the robot what it is doing, as the phone does.</summary>
    private RobotState? _state;

    public void Apply(RobotState s)
    {
        _state = s;
        StateText = Describe(s.State);
        ActionText = s.FullCleanAction switch
        {
            "VACUUMING" => T("Status_Vacuuming"),
            "VACUUMING_AND_MOPPING" => T("Status_VacuumingAndMopping"),
            "MOPPING" => T("Status_Mopping"),
            null or "NONE" => "",
            var a => a,
        };
        _dockState = s.DockState;
        UpdateDockText();
        DockBusy = s.IsDockBusy;
        WashDryLabel = s.DockState switch
        {
            "WASHING_MOP" => T("Status_StopWashing"),
            "DRYING_MOP" => T("Status_StopDrying"),
            "COLLECTING_DUST" => T("Status_StopEmptying"),
            _ => T("Status_WashAndDry2"),
        };
        if (s.BatteryChargeLevel is { } b) Battery = b;
        LastUpdate = DateTime.Now.ToString("HH:mm:ss", CultureInfo.CurrentCulture);

        var real = s.RealFaults.ToList();
        HasRealFault = real.Count > 0;
        FaultText = real.Count > 0
            ? string.Join(", ", real.Select(Describe))
            : "";

        CanPause = s.IsCleaning; // true whether running or already paused: this is the pause/resume toggle
        Paused = s.IsPaused;
        PauseResumeLabel = s.IsPaused ? T("Status_Resume2") : T("Status_Pause2");
        CanAbort = s.IsCleaning || s.IsPaused || s.IsMapping;

        if (s.Consumables is { } cons)
        {
            // Replace only the rows that changed: clearing and refilling on every state message
            // (several a minute while cleaning) re-created every row's visuals for nothing.
            var items = cons.Select(c => new ConsumableItem(DescribeConsumable(c.Type), c.Usage, c.NeedsRefill)).ToList();
            if (items.Count != Consumables.Count)
            {
                Consumables.Clear();
                foreach (var item in items) Consumables.Add(item);
            }
            else
            {
                for (var i = 0; i < items.Count; i++)
                    if (Consumables[i] != items[i]) Consumables[i] = items[i];
            }
        }
    }

    public void ApplyJdm(JdmProperties jdm)
    {
        ReturningToWash = jdm.BackToWash == true;
        // work_time lingers at its last value once drying is over (surplus 0, or stale if aborted),
        // so it only counts while the dock says it is drying.
        _dryingRemaining = jdm.StationAct == 2 && jdm.WorkTime is { RemainingSeconds: > 0 } t ? t.Remaining : null;
        UpdateDockText();
    }

    /// <summary>Classic dockState names the action; the jdm countdown, when there is one, says how long is left.</summary>
    private void UpdateDockText()
    {
        var text = DescribeDock(_dockState);
        if (_dockState == "DRYING_MOP" && _dryingRemaining is { } r)
            text += Tf("Status_Left", FormatRemaining(r));
        DockText = text;
    }

    [RelayCommand]
    private Task PauseResumeAsync() => _state?.IsPaused == true
        ? hub.RunAsync(T("Status_Resume"), c => c.ResumeAsync(_state.CurrentCleaningMode ?? "zoneConfigured"))
        : hub.RunAsync(T("Status_Pause"), c => c.PauseAsync());

    [RelayCommand]
    private Task AbortAsync() => hub.RunAsync(T("Status_ReturnToTheDock"),
        c => c.AbortAsync(_state?.State ?? "FULL_CLEAN_RUNNING", _state?.CurrentCleaningMode ?? "zoneConfigured"));

    [RelayCommand]
    private Task WashDryAsync() => DockBusy
        ? hub.RunAsync(T("Status_StoppingTheDockSAction"), c => c.StopDockActionAsync(_dockState))
        : hub.RunAsync(T("Status_WashAndDry"), c => c.WashAndDryMopAsync());

    [RelayCommand] private Task CollectDustAsync() => hub.RunAsync(T("Status_EmptyingTheBin"), c => c.CollectDustAsync());

    // ---- Text helpers ---------------------------------------------------------

    private static string Describe(string? state) => state switch
    {
        "INACTIVE_CHARGING" => T("Status_ChargingOnTheDock"),
        "INACTIVE_CHARGED" => T("Status_ChargedOnTheDock"),
        "INACTIVE_DISCHARGING" => T("Status_IdleOffTheDock"),
        "FULL_CLEAN_INITIATED" or "FULL_CLEAN_STARTING" => T("Status_Starting"),
        "FULL_CLEAN_DISCOVERING" => T("Status_LocatingItself"),
        "FULL_CLEAN_RUNNING" => T("Status_Cleaning"),
        "FULL_CLEAN_PAUSED" => T("Status_Paused"),
        "FULL_CLEAN_PAUSING" => T("Status_Pausing"),
        "FULL_CLEAN_RESUMING" => T("Status_Resuming"),
        "FULL_CLEAN_CHARGING" => T("Status_RechargingBeforeCarryingOn"),
        "FULL_CLEAN_NEEDS_CHARGE" => T("Status_BatteryTooLow"),
        "FULL_CLEAN_FINISHED" => T("Status_CleanFinished"),
        "FULL_CLEAN_ABORTED" or "ABORTED" => T("Status_CleanAbandoned"),
        "FULL_CLEAN_ABANDONED" => T("Status_CleanInterrupted"),
        "MAPPING_RUNNING" => T("Status_Mapping"),
        "MAPPING_FINISHED" => T("Status_MappingFinished"),
        null => "",
        var s => s,
    };

    private static string DescribeDock(string? dock) => dock switch
    {
        "IDLE" or null => "",
        "WASHING_MOP" => T("Status_DockWashingTheMopRoller"),
        "DRYING_MOP" => T("Status_DockDryingTheMopRoller"),
        "COLLECTING_DUST" => T("Status_DockEmptyingTheBin"),
        var d => Tf("Status_Dock", d),
    };

    private static string DescribeConsumable(string type) => type switch
    {
        "brushBar" => T("Status_BrushBar"),
        "mopRoller" => T("Status_MopRoller"),
        "sideBrushes" => T("Status_SideBrushes"),
        "robotFilter" => T("Status_RobotFilter"),
        "dockFilter" => T("Status_DockFilter"),
        "ioniserCartridge" => T("Status_IoniserCartridge"),
        "cleaningSolution" => T("Status_CleaningSolution"),
        var t => t,
    };

    private static string FormatRemaining(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes:D2} min" : $"{Math.Max(1, (int)Math.Ceiling(t.TotalMinutes))} min";
}
