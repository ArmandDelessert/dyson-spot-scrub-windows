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
    public string Display => Usage is { } ? $"{Remaining} %" : NeedsRefill == true ? T("à recharger", "to refill") : T("prêt", "ready");
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
    [ObservableProperty] private string _washDryLabel = T("Laver et sécher", "Wash and dry");
    [ObservableProperty] private string _pauseResumeLabel = T("Pause", "Pause");
    /// <summary>The clean is paused: the pause button resumes it, and shows it with its icon.</summary>
    [ObservableProperty] private bool _paused;

    public ObservableCollection<ConsumableItem> Consumables { get; } = new();

    private string? _dockState;
    private TimeSpan? _dryingRemaining;

    /// <summary>A fault in a few words: its code, and what it means when that is known (589, a locate failure; 2007, a room the robot cannot reach).</summary>
    public static string Describe(ActiveFault fault) =>
        T($"faute {fault.FaultCode}", $"fault {fault.FaultCode}") + fault.FaultCode switch
        {
            "589" => T(" (localisation impossible)", " (cannot locate itself)"),
            "2007" => T(" (pièce inaccessible)", " (room unreachable)"),
            _ => "",
        };

    public void Apply(RobotState s)
    {
        StateText = Describe(s.State);
        ActionText = s.FullCleanAction switch
        {
            "VACUUMING" => T("aspiration", "vacuuming"),
            "VACUUMING_AND_MOPPING" => T("aspiration et lavage", "vacuuming and mopping"),
            "MOPPING" => T("lavage", "mopping"),
            null or "NONE" => "",
            var a => a,
        };
        _dockState = s.DockState;
        UpdateDockText();
        DockBusy = s.IsDockBusy;
        WashDryLabel = s.DockState switch
        {
            "WASHING_MOP" => T("Arrêter le lavage", "Stop washing"),
            "DRYING_MOP" => T("Arrêter le séchage", "Stop drying"),
            "COLLECTING_DUST" => T("Arrêter le vidage", "Stop emptying"),
            _ => T("Laver et sécher", "Wash and dry"),
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
        PauseResumeLabel = s.IsPaused ? T("Reprendre", "Resume") : T("Pause", "Pause");
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
            text += T($", {FormatRemaining(r)} restantes", $", {FormatRemaining(r)} left");
        DockText = text;
    }

    [RelayCommand]
    private Task PauseResumeAsync() => hub.Session?.Tracker.State?.IsPaused == true
        ? hub.RunAsync(T("reprise", "resume"), c => c.ResumeAsync(hub.Session!.Tracker.State?.CurrentCleaningMode ?? "zoneConfigured"))
        : hub.RunAsync(T("pause", "pause"), c => c.PauseAsync());

    [RelayCommand]
    private Task AbortAsync() => hub.RunAsync(T("retour à la station", "return to the dock"), async c =>
    {
        var st = hub.Session!.Tracker.State;
        await c.AbortAsync(st?.State ?? "FULL_CLEAN_RUNNING", st?.CurrentCleaningMode ?? "zoneConfigured");
    });

    [RelayCommand]
    private Task WashDryAsync() => DockBusy
        ? hub.RunAsync(T("arrêt de l'action de la station", "stopping the dock's action"), c => c.StopDockActionAsync(_dockState))
        : hub.RunAsync(T("laver et sécher", "wash and dry"), c => c.WashAndDryMopAsync());

    [RelayCommand] private Task CollectDustAsync() => hub.RunAsync(T("vidage du collecteur", "emptying the bin"), c => c.CollectDustAsync());

    // ---- Text helpers ---------------------------------------------------------

    private static string Describe(string? state) => state switch
    {
        "INACTIVE_CHARGING" => T("En charge sur la station", "Charging on the dock"),
        "INACTIVE_CHARGED" => T("Chargé, sur la station", "Charged, on the dock"),
        "INACTIVE_DISCHARGING" => T("Au repos, hors station", "Idle, off the dock"),
        "FULL_CLEAN_INITIATED" or "FULL_CLEAN_STARTING" => T("Démarrage", "Starting"),
        "FULL_CLEAN_DISCOVERING" => T("Localisation", "Locating itself"),
        "FULL_CLEAN_RUNNING" => T("Nettoyage en cours", "Cleaning"),
        "FULL_CLEAN_PAUSED" => T("En pause", "Paused"),
        "FULL_CLEAN_PAUSING" => T("Mise en pause", "Pausing"),
        "FULL_CLEAN_RESUMING" => T("Reprise", "Resuming"),
        "FULL_CLEAN_CHARGING" => T("Recharge avant de continuer", "Recharging before carrying on"),
        "FULL_CLEAN_NEEDS_CHARGE" => T("Batterie insuffisante", "Battery too low"),
        "FULL_CLEAN_FINISHED" => T("Nettoyage terminé", "Clean finished"),
        "FULL_CLEAN_ABORTED" or "ABORTED" => T("Nettoyage abandonné", "Clean abandoned"),
        "FULL_CLEAN_ABANDONED" => T("Nettoyage interrompu", "Clean interrupted"),
        "MAPPING_RUNNING" => T("Cartographie en cours", "Mapping"),
        "MAPPING_FINISHED" => T("Cartographie terminée", "Mapping finished"),
        null => "",
        var s => s,
    };

    private static string DescribeDock(string? dock) => dock switch
    {
        "IDLE" or null => "",
        "WASHING_MOP" => T("Station : lavage du rouleau humide", "Dock: washing the mop roller"),
        "DRYING_MOP" => T("Station : séchage du rouleau humide", "Dock: drying the mop roller"),
        "COLLECTING_DUST" => T("Station : vidage du collecteur", "Dock: emptying the bin"),
        var d => T($"Station : {d}", $"Dock: {d}"),
    };

    private static string DescribeConsumable(string type) => type switch
    {
        "brushBar" => T("Brosse", "Brush bar"),
        "mopRoller" => T("Rouleau humide", "Mop roller"),
        "sideBrushes" => T("Brosses latérales", "Side brushes"),
        "robotFilter" => T("Filtre du robot", "Robot filter"),
        "dockFilter" => T("Filtre de la station", "Dock filter"),
        "ioniserCartridge" => T("Cartouche ioniseur", "Ioniser cartridge"),
        "cleaningSolution" => T("Produit de nettoyage", "Cleaning solution"),
        var t => t,
    };

    private static string FormatRemaining(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes:D2} min" : $"{Math.Max(1, (int)Math.Ceiling(t.TotalMinutes))} min";
}
