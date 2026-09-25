using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MyDyson.App.Services;
using MyDyson.Core;

namespace MyDyson.App.ViewModels;

/// <summary>One row of the schedule list.</summary>
public sealed class ScheduleItem(CleaningSchedule schedule, string roomsText, int minutes, string overlap)
{
    public CleaningSchedule Schedule { get; } = schedule;
    public bool Enabled => Schedule.Enabled;
    public string TimeText => ScheduleDayLabels.Time(Schedule.Hour, Schedule.Minute);
    public string DaysText => char.ToUpper(ScheduleDayLabels.Describe(Schedule.Days)[0], CultureInfo.CurrentCulture) + ScheduleDayLabels.Describe(Schedule.Days)[1..];
    public string RoomsText { get; } = roomsText;
    public string DurationText { get; } = minutes > 0 ? $"environ {ScheduleEditorViewModel.FormatMinutes(minutes)}" : "";
    public string OverlapText { get; } = overlap;
}

/// <summary>
/// The Horaires tab. The robot runs the schedules of whichever map is active and never tells them
/// back — get_order only answers how many there are — so this lists the ones created here (see
/// <see cref="ScheduleStore"/>) and says when the robot counts more, the phone's own being
/// invisible. Like the map edits, everything is kept to the active map: a schedule names its map,
/// but none was ever seen created or changed for another one.
/// </summary>
public sealed partial class SchedulesViewModel : ObservableObject
{
    private readonly RobotHub _hub;
    private readonly MapCatalog _maps;
    private readonly ScheduleStore _store;
    private readonly Func<IEnumerable<CleanSummary>> _history;
    private ScheduleSummary? _robotSummary;
    private string? _robotTimeZone;
    private bool _timeZoneLoaded;

    public SchedulesViewModel(RobotHub hub, MapCatalog maps, ScheduleStore store, Func<IEnumerable<CleanSummary>> history)
    {
        _hub = hub;
        _maps = maps;
        _store = store;
        _history = history;
        // Whatever reloads the maps — start-up, Actualiser, an edit in the map manager — also says
        // which map is active now.
        maps.Maps.CollectionChanged += (_, _) => Refresh();
        hub.PropertyChanged += OnHubChanged;
    }

    public ObservableCollection<ScheduleItem> Items { get; } = new();
    [ObservableProperty] private MapItem? _activeMap;
    [ObservableProperty] private string _heading = "Aucune carte active";
    [ObservableProperty] private string _robotCountText = "";
    /// <summary>The robot counts a different number of schedules than this list: some were made or removed on the phone.</summary>
    [ObservableProperty] private string _syncNotice = "";
    [ObservableProperty] private string _otherMapsText = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(NewCommand), nameof(EditCommand), nameof(DeleteCommand), nameof(ToggleCommand))]
    private bool _busy;

    /// <summary>Shows the editor and returns whether the user confirmed it; set by the window.</summary>
    public Func<ScheduleEditorViewModel, bool>? EditSchedule { get; set; }
    public Func<string, string, bool>? Confirm { get; set; }

    private void OnHubChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(RobotHub.Connected)) return;
        NewCommand.NotifyCanExecuteChanged();
        EditCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
        ToggleCommand.NotifyCanExecuteChanged();
    }

    // ---- What is shown ------------------------------------------------------------------

    /// <summary>Rebuilds the list for the map that is active now.</summary>
    public void Refresh()
    {
        ActiveMap = _maps.Maps.FirstOrDefault(m => m.Metadata.IsCurrentMap);
        Heading = ActiveMap is { } map ? $"Carte active : {map.Metadata.Name ?? map.Id}" : "Aucune carte active";

        var all = _store.For(_hub.Serial);
        var mine = all.Where(s => IsOnActiveMap(s)).OrderBy(s => s.Hour * 60 + s.Minute).ToList();
        var rate = CleanDurationEstimate.MinutesPerSquareMetre(_history());
        Items.Clear();
        foreach (var s in mine)
        {
            var minutes = MinutesOf(s, rate);
            var overlap = mine.FirstOrDefault(o => o.Id != s.Id && o.Enabled && s.Enabled
                && CleanDurationEstimate.Overlaps(o, MinutesOf(o, rate), s));
            Items.Add(new ScheduleItem(s, RoomsText(s), minutes,
                overlap is null ? "" : $"Risque de ne pas démarrer : l'horaire de {ScheduleDayLabels.Time(overlap.Hour, overlap.Minute)} sera sans doute encore en cours."));
        }

        var others = all.Count - mine.Count;
        OtherMapsText = others == 0 ? ""
            : $"{others} autre{(others > 1 ? "s" : "")} horaire{(others > 1 ? "s" : "")} créé{(others > 1 ? "s" : "")} ici pour d'autres cartes : "
              + "ils réapparaissent quand leur carte redevient active.";
        UpdateRobotCount();
        NewCommand.NotifyCanExecuteChanged();
    }

    private bool IsOnActiveMap(CleaningSchedule s) => ActiveMap?.Id == s.MapId.ToString(CultureInfo.InvariantCulture);

    /// <summary>order_total, pushed by the robot after every schedule change and every change of active map.</summary>
    public void ApplyJdm(JdmProperties jdm)
    {
        if (jdm.OrderTotal is { } total && (total.Total, total.Enabled) != (_robotSummary?.Total, _robotSummary?.Enabled))
        {
            _robotSummary = total;
            UpdateRobotCount();
        }
    }

    /// <summary>Asks the robot how many schedules the active map has, for the count shown under the heading.</summary>
    public async Task LoadRobotSummaryAsync()
    {
        if (_hub.Session?.Client is not { IsConnected: true } client) return;
        try
        {
            if (await client.GetScheduleSummaryAsync(_hub.Ct) is { } summary)
            {
                _robotSummary = summary;
                UpdateRobotCount();
            }
        }
        catch (OperationCanceledException) when (_hub.IsShuttingDown) { }
        catch (Exception ex) { _hub.AddLog($"horaires: {ex.Message}"); }
    }

    private void UpdateRobotCount()
    {
        if (_robotSummary is not { } robot || ActiveMap is null)
        {
            RobotCountText = "";
            SyncNotice = "";
            return;
        }
        RobotCountText = robot.Total switch
        {
            0 => "Le robot n'a aucun horaire pour cette carte.",
            1 => $"Le robot a 1 horaire pour cette carte, {(robot.Enabled == 1 ? "activé" : "désactivé")}.",
            var n => $"Le robot a {n} horaires pour cette carte, dont {robot.Enabled} activé{(robot.Enabled > 1 ? "s" : "")}.",
        };
        var known = Items.Count;
        SyncNotice = robot.Total == known ? ""
            : robot.Total > known
                ? $"{robot.Total - known} de ces horaires ont été créés depuis l'application mobile : le robot ne permet pas de les relire, ils ne peuvent donc pas figurer ici."
                : "Le robot compte moins d'horaires que cette liste : certains ont sans doute été supprimés depuis l'application mobile.";
    }

    private string RoomsText(CleaningSchedule s)
    {
        var zones = ActiveMap?.Metadata.Zones ?? [];
        var names = s.Rooms.Select(r => zones.FirstOrDefault(z => z.Id == r.ZoneId.ToString(CultureInfo.InvariantCulture)) is { } z
            ? RoomTypeLabels.Resolve(string.IsNullOrEmpty(z.Type) ? null : z.Type, z.Name, z.Id)
            : $"pièce {r.ZoneId} (disparue)");
        return string.Join(", ", names);
    }

    private int MinutesOf(CleaningSchedule s, double rate)
    {
        var zones = ActiveMap?.Metadata.Zones ?? [];
        return CleanDurationEstimate.Minutes(
            s.Rooms.Select(r => (zones.FirstOrDefault(z => z.Id == r.ZoneId.ToString(CultureInfo.InvariantCulture))?.Area ?? 0, r.Settings)), rate);
    }

    // ---- Commands --------------------------------------------------------------------------

    private bool CanCreate() => ActiveMap is not null && _hub.Connected && !Busy;
    private bool CanChange(ScheduleItem? item) => item is not null && _hub.Connected && !Busy;

    [RelayCommand(CanExecute = nameof(CanCreate))]
    private async Task NewAsync()
    {
        if (NewEditor() is not { } editor || EditSchedule is null) return;
        if (!EditSchedule(editor)) return;
        var schedule = editor.Build(CleaningSchedule.NewId());
        await SaveAsync(schedule, $"horaire de {ScheduleDayLabels.Time(schedule.Hour, schedule.Minute)} créé");
    }

    [RelayCommand(CanExecute = nameof(CanChange))]
    private async Task EditAsync(ScheduleItem? item)
    {
        if (item is null || ActiveMap is not { } map || EditSchedule is null) return;
        var editor = new ScheduleEditorViewModel(map, item.Schedule, MineOnActiveMap(), CleanDurationEstimate.MinutesPerSquareMetre(_history()));
        if (!EditSchedule(editor)) return;
        await SaveAsync(editor.Build(item.Schedule.Id), "horaire modifié");
    }

    /// <summary>The check box of a row: add_order again with only "enable" changed, which is how the phone does it.</summary>
    [RelayCommand(CanExecute = nameof(CanChange))]
    private async Task ToggleAsync(ScheduleItem? item)
    {
        if (item is null) return;
        var schedule = item.Schedule with { Enabled = !item.Schedule.Enabled };
        await SaveAsync(schedule, schedule.Enabled ? "horaire activé" : "horaire désactivé");
    }

    [RelayCommand(CanExecute = nameof(CanChange))]
    private async Task DeleteAsync(ScheduleItem? item)
    {
        if (item is null) return;
        if (Confirm?.Invoke("Supprimer l'horaire", $"Supprimer l'horaire de {item.TimeText} ({ScheduleDayLabels.Describe(item.Schedule.Days)}) ?") != true) return;

        var done = await SendAsync("horaire supprimé", c => c.DeleteScheduleAsync(item.Schedule.Id, _hub.Ct));
        // Refused most likely means the robot no longer has it: deleted from the phone, or with its map.
        if (done == false && Confirm?.Invoke("Supprimer l'horaire",
                "Le robot a refusé la suppression : il ne connaît sans doute plus cet horaire. Le retirer quand même de cette liste ?") == true)
            done = true;
        if (done != true) return;
        _store.Remove(_hub.Serial, item.Schedule.Id);
        Refresh();
    }

    /// <summary>An editor for a new schedule of the active map, or null when there is none.</summary>
    public ScheduleEditorViewModel? NewEditor() => ActiveMap is { } map
        ? new ScheduleEditorViewModel(map, null, MineOnActiveMap(), CleanDurationEstimate.MinutesPerSquareMetre(_history()))
        : null;

    private List<CleaningSchedule> MineOnActiveMap() => _store.For(_hub.Serial).Where(IsOnActiveMap).ToList();

    private async Task SaveAsync(CleaningSchedule schedule, string label)
    {
        if (ActiveMap is not { } map) return;
        var rooms = (map.Metadata.Zones ?? [])
            .Where(z => int.TryParse(z.Id, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            .Select(z => new MapRoom(int.Parse(z.Id, CultureInfo.InvariantCulture), z.Name ?? ""))
            .ToList();
        var offset = await TimeZoneOffsetAsync();
        if (await SendAsync(label, c => c.SaveScheduleAsync(schedule, rooms, offset, _hub.Ct)) != true) return;
        _store.Save(_hub.Serial, schedule);
        Refresh();
    }

    /// <summary>
    /// The robot's time zone offset, worked out as the phone does from the zone the cloud holds for
    /// the robot; this machine's own offset if that cannot be read.
    /// </summary>
    private async Task<int> TimeZoneOffsetAsync()
    {
        if (!_timeZoneLoaded)
        {
            try { _robotTimeZone = await _hub.Api.GetTimeZoneAsync(_hub.Serial, _hub.Ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { _hub.AddLog($"fuseau horaire: {ex.Message}"); }
            _timeZoneLoaded = true;
        }
        var now = DateTimeOffset.UtcNow;
        return CleaningSchedule.TimeZoneOffsetSeconds(_robotTimeZone, now) ?? (int)TimeZoneInfo.Local.GetUtcOffset(now).TotalSeconds;
    }

    /// <summary>True when the robot accepted, false when it refused, null when nothing could be sent.</summary>
    private async Task<bool?> SendAsync(string label, Func<RobotMqttClient, Task<bool>> send)
    {
        if (_hub.Session?.Client is not { IsConnected: true } client) { Status = "Robot non connecté."; return null; }
        Busy = true;
        Status = "";
        try
        {
            if (!await send(client))
            {
                Status = "Le robot a refusé.";
                _hub.AddLog($"{label} : refusé par le robot");
                return false;
            }
            _hub.AddLog(label);
            Status = char.ToUpper(label[0], CultureInfo.CurrentCulture) + label[1..] + ".";
            return true;
        }
        catch (OperationCanceledException) when (_hub.IsShuttingDown) { return null; }
        catch (Exception ex)
        {
            Status = ex.Message;
            _hub.AddLog($"{label} : {ex.Message}");
            return null;
        }
        finally { Busy = false; }
    }
}
