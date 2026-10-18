using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DyssCockpit.Core;

using static DyssCockpit.Core.Translation;

namespace DyssCockpit.Presentation.ViewModels;

public sealed record BackWashOption(string Key, string Label, string? Description);
public sealed record DryOption(int Hours, string Label, string Description);

/// <summary>
/// The robot settings page (Réglages du robot), with the official app's wording. Each control writes to the robot as soon as
/// the user changes it; values the robot pushes back through <see cref="Apply"/> must not trigger
/// that write again, hence <see cref="_applyingState"/>.
/// </summary>
public sealed partial class RobotSettingsViewModel(RobotHub hub) : ObservableObject
{
    private bool _applyingState;

    [ObservableProperty] private bool _hotWaterMop;
    [ObservableProperty] private bool _detergent;
    [ObservableProperty] private string _solutionStatus = "";
    [ObservableProperty] private bool _hotWaterSwitch;
    [ObservableProperty] private BackWashOption? _backWash;
    [ObservableProperty] private DryOption? _dryDuration;
    [ObservableProperty] private bool _alarm;
    [ObservableProperty] private int _volume;
    [ObservableProperty] private bool _washMopBeforeClean;
    [ObservableProperty] private bool _washMopBeforeCleanKnown;

    public IReadOnlyList<BackWashOption> BackWashOptions { get; } =
    [
        new("ROOM", T("RobotSettings_AfterEachRoom"), null),
        new("TIME15", T("RobotSettings_Every15Min"), null),
        new("TIME30", T("RobotSettings_Every30Min"), null),
        new("ONLY_WHEN_NEEDED", T("RobotSettings_OnlyWhenNeeded"), T("RobotSettings_TheRobotWillOnlyGo")),
    ];

    public IReadOnlyList<DryOption> DryOptions { get; } =
    [
        new(3, T("RobotSettings_3Hours"), T("RobotSettings_BestForDocksInDry")),
        new(4, T("RobotSettings_4Hours"), T("RobotSettings_BestForDocksInSlightly")),
        new(5, T("RobotSettings_5Hours"), T("RobotSettings_BestForDocksInVery")),
    ];

    public void Apply(RobotState s)
    {
        _applyingState = true;
        try
        {
            if (s.HotWaterMop is { } hwm) HotWaterMop = hwm;
            if (s.HotWaterSwitch is { } hws) HotWaterSwitch = hws;
            if (s.Detergent is { } det) Detergent = det;
            if (s.Alarm is { } al) Alarm = al;
            if (s.Volume is { } vol) Volume = vol;
            if (s.WashMopBeforeClean is { } wm) { WashMopBeforeClean = wm; WashMopBeforeCleanKnown = true; }
            if (s.AirDryFrequency is { } adf) DryDuration = DryOptions.FirstOrDefault(o => o.Hours == adf) ?? DryDuration;
            BackWash = s.BackWashType switch
            {
                "ROOM" => BackWashOptions[0],
                "TIME" when s.BackWashTime is 30 => BackWashOptions[2],
                "TIME" => BackWashOptions[1],
                "ONLY_WHEN_NEEDED" => BackWashOptions[3],
                _ => BackWash,
            };
            if (s.Consumables?.FirstOrDefault(c => c.Type == "cleaningSolution") is { } solution)
                SolutionStatus = solution.NeedsRefill == true ? T("RobotSettings_ToRefill") : T("RobotSettings_ReadyToUse");
        }
        finally
        {
            _applyingState = false;
        }
    }

    // Only react to user changes, not to values coming from the robot.
    partial void OnHotWaterMopChanged(bool value) { if (!_applyingState) _ = hub.RunAsync(T("RobotSettings_MopWithHotWater"), c => c.SetHotWaterMopAsync(value)); }
    partial void OnDetergentChanged(bool value) { if (!_applyingState) _ = hub.RunAsync(T("RobotSettings_MopWithCleaningSolution"), c => c.SetDetergentAsync(value)); }
    partial void OnHotWaterSwitchChanged(bool value) { if (!_applyingState) _ = hub.RunAsync(T("RobotSettings_SelfCleanWithHotWater"), c => c.SetHotWaterSwitchAsync(value)); }
    partial void OnAlarmChanged(bool value) { if (!_applyingState) _ = hub.RunAsync(T("RobotSettings_Sounds"), c => c.SetAlarmAsync(value)); }
    partial void OnWashMopBeforeCleanChanged(bool value) { if (!_applyingState) _ = hub.RunAsync(T("RobotSettings_ExtendThePreparationClassicMessage"), c => c.SetWashMopBeforeCleanAsync(value)); }
    partial void OnDryDurationChanged(DryOption? value) { if (!_applyingState && value is not null) _ = hub.RunAsync(Tf("RobotSettings_DryingH", value.Hours), c => c.SetAirDryFrequencyAsync(value.Hours)); }
    partial void OnBackWashChanged(BackWashOption? value)
    {
        if (_applyingState || value is null) return;
        _ = hub.RunAsync(Tf("RobotSettings_Interval", value.Label), c => value.Key switch
        {
            "ROOM" => c.SetBackWashPerRoomAsync(),
            "TIME15" => c.SetBackWashByTimeAsync(15),
            "TIME30" => c.SetBackWashByTimeAsync(30),
            _ => c.SetBackWashOnlyWhenNeededAsync(),
        });
    }

    [RelayCommand] private Task ApplyVolumeAsync() => hub.RunAsync($"volume {Volume}", c => c.SetVolumeAsync(Volume));
}
