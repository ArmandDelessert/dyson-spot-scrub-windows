using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dyss.Core;

namespace Dyss.Presentation.ViewModels;

public sealed record BackWashOption(string Key, string Label, string? Description);
public sealed record DryOption(int Hours, string Label, string Description);

/// <summary>
/// The Réglages tab, with the official app's wording. Each control writes to the robot as soon as
/// the user changes it; values the robot pushes back through <see cref="Apply"/> must not trigger
/// that write again, hence <see cref="_applyingState"/>.
/// </summary>
public sealed partial class SettingsViewModel(RobotHub hub) : ObservableObject
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
        new("ROOM", "Après chaque pièce", null),
        new("TIME15", "Toutes les 15 min", null),
        new("TIME30", "Toutes les 30 min", null),
        new("ONLY_WHEN_NEEDED", "Uniquement si nécessaire", "Le robot retournera à la station d'accueil uniquement lorsqu'il devra remplir ou vider ses réservoirs."),
    ];

    public IReadOnlyList<DryOption> DryOptions { get; } =
    [
        new(3, "3 heures", "Idéal pour les stations placées dans des zones sèches et bien ventilées."),
        new(4, "4 heures", "Idéal pour les stations placées dans des zones légèrement humides."),
        new(5, "5 heures", "Idéal pour les stations placées dans des zones très humides ou peu ventilées."),
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
                SolutionStatus = solution.NeedsRefill == true ? "À recharger" : "Prêt à l'emploi";
        }
        finally
        {
            _applyingState = false;
        }
    }

    // Only react to user changes, not to values coming from the robot.
    partial void OnHotWaterMopChanged(bool value) { if (!_applyingState) _ = hub.RunAsync("laver à l'eau chaude", c => c.SetHotWaterMopAsync(value)); }
    partial void OnDetergentChanged(bool value) { if (!_applyingState) _ = hub.RunAsync("laver avec le produit", c => c.SetDetergentAsync(value)); }
    partial void OnHotWaterSwitchChanged(bool value) { if (!_applyingState) _ = hub.RunAsync("autonettoyage à l'eau chaude", c => c.SetHotWaterSwitchAsync(value)); }
    partial void OnAlarmChanged(bool value) { if (!_applyingState) _ = hub.RunAsync("sons", c => c.SetAlarmAsync(value)); }
    partial void OnWashMopBeforeCleanChanged(bool value) { if (!_applyingState) _ = hub.RunAsync("prolonger les préparatifs (message classique seul)", c => c.SetWashMopBeforeCleanAsync(value)); }
    partial void OnDryDurationChanged(DryOption? value) { if (!_applyingState && value is not null) _ = hub.RunAsync($"séchage {value.Hours} h", c => c.SetAirDryFrequencyAsync(value.Hours)); }
    partial void OnBackWashChanged(BackWashOption? value)
    {
        if (_applyingState || value is null) return;
        _ = hub.RunAsync($"intervalle: {value.Label}", c => value.Key switch
        {
            "ROOM" => c.SetBackWashPerRoomAsync(),
            "TIME15" => c.SetBackWashByTimeAsync(15),
            "TIME30" => c.SetBackWashByTimeAsync(30),
            _ => c.SetBackWashOnlyWhenNeededAsync(),
        });
    }

    [RelayCommand] private Task ApplyVolumeAsync() => hub.RunAsync($"volume {Volume}", c => c.SetVolumeAsync(Volume));
}
