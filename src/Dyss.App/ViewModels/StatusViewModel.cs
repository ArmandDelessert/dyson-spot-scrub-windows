using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dyss.Core;

namespace Dyss.App.ViewModels;

/// <summary>Consumable as shown by the app: percentage of life left, replace at 0.</summary>
public sealed record ConsumableItem(string Name, int? Usage, bool? NeedsRefill)
{
    public int Remaining => Usage is { } u ? Math.Clamp(100 - u, 0, 100) : 100;
    public string Display => Usage is { } ? $"{Remaining} %" : NeedsRefill == true ? "à recharger" : "prêt";
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
    [ObservableProperty] private string _washDryLabel = "Laver et sécher";
    [ObservableProperty] private string _pauseResumeLabel = "⏸ Pause";

    public ObservableCollection<ConsumableItem> Consumables { get; } = new();

    private string? _dockState;
    private TimeSpan? _dryingRemaining;

    public void Apply(RobotState s)
    {
        StateText = Describe(s.State);
        ActionText = s.FullCleanAction switch
        {
            "VACUUMING" => "aspiration",
            "VACUUMING_AND_MOPPING" => "aspiration et lavage",
            "MOPPING" => "lavage",
            null or "NONE" => "",
            var a => a,
        };
        _dockState = s.DockState;
        UpdateDockText();
        DockBusy = s.IsDockBusy;
        WashDryLabel = s.DockState switch
        {
            "WASHING_MOP" => "Arrêter le lavage",
            "DRYING_MOP" => "Arrêter le séchage",
            "COLLECTING_DUST" => "Arrêter le vidage",
            _ => "Laver et sécher",
        };
        if (s.BatteryChargeLevel is { } b) Battery = b;
        LastUpdate = DateTime.Now.ToString("HH:mm:ss", CultureInfo.CurrentCulture);

        var real = s.RealFaults.ToList();
        HasRealFault = real.Count > 0;
        FaultText = real.Count > 0
            ? string.Join(", ", real.Select(f => $"faute {f.FaultCode}" + (f.FaultCode == "589" ? " (localisation impossible)" : "")))
            : "";

        CanPause = s.IsCleaning; // true whether running or already paused: this is the pause/resume toggle
        PauseResumeLabel = s.IsPaused ? "▶ Reprendre" : "⏸ Pause";
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
            text += $", {FormatRemaining(r)} restantes";
        DockText = text;
    }

    [RelayCommand]
    private Task PauseResumeAsync() => hub.Session?.Tracker.State?.IsPaused == true
        ? hub.RunAsync("reprise", c => c.ResumeAsync(hub.Session!.Tracker.State?.CurrentCleaningMode ?? "zoneConfigured"))
        : hub.RunAsync("pause", c => c.PauseAsync());

    [RelayCommand]
    private Task AbortAsync() => hub.RunAsync("retour à la station", async c =>
    {
        var st = hub.Session!.Tracker.State;
        await c.AbortAsync(st?.State ?? "FULL_CLEAN_RUNNING", st?.CurrentCleaningMode ?? "zoneConfigured");
    });

    [RelayCommand]
    private Task WashDryAsync() => DockBusy
        ? hub.RunAsync("arrêt de l'action de la station", c => c.StopDockActionAsync(_dockState))
        : hub.RunAsync("laver et sécher", c => c.WashAndDryMopAsync());

    [RelayCommand] private Task CollectDustAsync() => hub.RunAsync("vidage du collecteur", c => c.CollectDustAsync());

    // ---- Text helpers ---------------------------------------------------------

    private static string Describe(string? state) => state switch
    {
        "INACTIVE_CHARGING" => "En charge sur la station",
        "INACTIVE_CHARGED" => "Chargé, sur la station",
        "INACTIVE_DISCHARGING" => "Au repos, hors station",
        "FULL_CLEAN_INITIATED" or "FULL_CLEAN_STARTING" => "Démarrage",
        "FULL_CLEAN_DISCOVERING" => "Localisation",
        "FULL_CLEAN_RUNNING" => "Nettoyage en cours",
        "FULL_CLEAN_PAUSED" => "En pause",
        "FULL_CLEAN_PAUSING" => "Mise en pause",
        "FULL_CLEAN_RESUMING" => "Reprise",
        "FULL_CLEAN_CHARGING" => "Recharge avant de continuer",
        "FULL_CLEAN_NEEDS_CHARGE" => "Batterie insuffisante",
        "FULL_CLEAN_FINISHED" => "Nettoyage terminé",
        "FULL_CLEAN_ABORTED" or "ABORTED" => "Nettoyage abandonné",
        "FULL_CLEAN_ABANDONED" => "Nettoyage interrompu",
        "MAPPING_RUNNING" => "Cartographie en cours",
        "MAPPING_FINISHED" => "Cartographie terminée",
        null => "",
        var s => s,
    };

    private static string DescribeDock(string? dock) => dock switch
    {
        "IDLE" or null => "",
        "WASHING_MOP" => "Station : lavage du rouleau humide",
        "DRYING_MOP" => "Station : séchage du rouleau humide",
        "COLLECTING_DUST" => "Station : vidage du collecteur",
        var d => $"Station : {d}",
    };

    private static string DescribeConsumable(string type) => type switch
    {
        "brushBar" => "Brosse",
        "mopRoller" => "Rouleau humide",
        "sideBrushes" => "Brosses latérales",
        "robotFilter" => "Filtre du robot",
        "dockFilter" => "Filtre de la station",
        "ioniserCartridge" => "Cartouche ioniseur",
        "cleaningSolution" => "Produit de nettoyage",
        var t => t,
    };

    private static string FormatRemaining(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes:D2} min" : $"{Math.Max(1, (int)Math.Ceiling(t.TotalMinutes))} min";
}
