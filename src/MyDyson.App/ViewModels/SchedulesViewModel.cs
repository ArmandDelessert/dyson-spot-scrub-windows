using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
/// The Horaires tab: the schedules of the active map, read from the cloud's scheduler — the same
/// list the phone shows, whoever created them. Changes go to the robot (add_order, del_order), which
/// the cloud picks up; the robot's order_total push after each change, from here or from the
/// phone, is the cue to read the list again. Like the phone, only the active map's schedules exist
/// here: the cloud swaps the list when the active map changes.
/// </summary>
public sealed partial class SchedulesViewModel : ObservableObject
{
    private readonly RobotHub _hub;
    private readonly MapCatalog _maps;
    private readonly Func<IEnumerable<CleanSummary>> _history;
    private List<CleaningSchedule> _schedules = [];
    private ScheduleSummary? _robotSummary;
    private CancellationTokenSource? _pendingReload;
    private string? _robotTimeZone;
    private bool _timeZoneLoaded;

    /// <summary>How long to wait after the robot reports a change before reading the cloud's list again, which trails it a little.</summary>
    public TimeSpan ReloadDelay { get; set; } = TimeSpan.FromSeconds(2);

    public SchedulesViewModel(RobotHub hub, MapCatalog maps, Func<IEnumerable<CleanSummary>> history)
    {
        _hub = hub;
        _maps = maps;
        _history = history;
        // Whatever reloads the maps — start-up, Actualiser, an edit in the map manager — also says
        // which map is active now.
        maps.Maps.CollectionChanged += (_, _) => OnMapsChanged();
        hub.PropertyChanged += OnHubChanged;
    }

    public ObservableCollection<ScheduleItem> Items { get; } = new();
    [ObservableProperty] private MapItem? _activeMap;
    [ObservableProperty] private string _heading = "Aucune carte active";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _emptyText = "";
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

    private void OnMapsChanged()
    {
        var active = _maps.Maps.FirstOrDefault(m => m.Metadata.IsCurrentMap);
        var changed = active?.Id != ActiveMap?.Id;
        ActiveMap = active;
        Heading = active is { } map ? $"Carte active : {map.Metadata.Name ?? map.Id}" : "Aucune carte active";
        NewCommand.NotifyCanExecuteChanged();
        if (changed) ScheduleReload(TimeSpan.Zero);
        else Rebuild();   // same map, but its rooms (names, areas) may have changed
    }

    /// <summary>Reads the active map's schedules from the cloud.</summary>
    public async Task LoadAsync()
    {
        if (ActiveMap is not { } map)
        {
            _schedules = [];
            Rebuild();
            return;
        }
        try
        {
            var events = await _hub.Api.GetScheduleEventsAsync(_hub.Serial, _hub.ProductType, _hub.Ct);
            // The list is the active map's; a stray event of another map (the switch not yet seen) is left out.
            _schedules = (events.Events ?? []).Select(e => e.ToSchedule())
                .Where(s => s is not null && s.MapId.ToString(CultureInfo.InvariantCulture) == map.Id)
                .Select(s => s!).ToList();
            Rebuild();
        }
        catch (OperationCanceledException) when (_hub.IsShuttingDown) { }
        catch (Exception ex)
        {
            Status = $"Horaires : {ex.Message}";
            _hub.AddLog($"horaires: {ex.Message}");
        }
    }

    /// <summary>
    /// order_total, pushed by the robot after every schedule change, whoever made it, and every
    /// change of active map: the cloud's list is read again a moment later.
    /// </summary>
    public void ApplyJdm(JdmProperties jdm)
    {
        if (jdm.OrderTotal is not { } total || (total.Total, total.Enabled) == (_robotSummary?.Total, _robotSummary?.Enabled)) return;
        var first = _robotSummary is null;
        _robotSummary = total;
        if (!first) ScheduleReload(ReloadDelay);
    }

    private void ScheduleReload(TimeSpan delay)
    {
        _pendingReload?.Cancel();
        _pendingReload?.Dispose();
        var pending = _pendingReload = CancellationTokenSource.CreateLinkedTokenSource(_hub.Ct);
        _ = ReloadAfterAsync(delay, pending.Token);
    }

    private async Task ReloadAfterAsync(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            if (delay > TimeSpan.Zero) await Task.Delay(delay, ct);
            await LoadAsync();
        }
        catch (OperationCanceledException) { }   // superseded by a newer reload, or shutting down
    }

    private void Rebuild()
    {
        var mine = _schedules.OrderBy(s => s.Hour * 60 + s.Minute).ThenBy(s => s.Id).ToList();
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
        EmptyText = ActiveMap is not null && Items.Count == 0 ? "Aucun horaire pour cette carte." : "";
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
        var editor = new ScheduleEditorViewModel(map, item.Schedule, _schedules, CleanDurationEstimate.MinutesPerSquareMetre(_history()));
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
        if (done == true) _schedules = [.. _schedules.Where(s => s.Id != item.Schedule.Id)];
        AfterChange();
    }

    /// <summary>An editor for a new schedule of the active map, or null when there is none.</summary>
    public ScheduleEditorViewModel? NewEditor() => ActiveMap is { } map
        ? new ScheduleEditorViewModel(map, null, _schedules, CleanDurationEstimate.MinutesPerSquareMetre(_history()))
        : null;

    private async Task SaveAsync(CleaningSchedule schedule, string label)
    {
        if (ActiveMap is not { } map) return;
        var rooms = (map.Metadata.Zones ?? [])
            .Where(z => int.TryParse(z.Id, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            .Select(z => new MapRoom(int.Parse(z.Id, CultureInfo.InvariantCulture), z.Name ?? ""))
            .ToList();
        var offset = await TimeZoneOffsetAsync();
        if (await SendAsync(label, c => c.SaveScheduleAsync(schedule, rooms, offset, _hub.Ct)) == true)
            _schedules = [.. _schedules.Where(s => s.Id != schedule.Id), schedule];
        AfterChange();
    }

    /// <summary>
    /// Shows the change at once, then reads the cloud's list again: its copy follows the robot a
    /// moment later, and a refused change must come back as it really is.
    /// </summary>
    private void AfterChange()
    {
        Rebuild();
        ScheduleReload(ReloadDelay);
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
