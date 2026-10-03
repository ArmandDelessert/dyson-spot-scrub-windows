using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dyss.Core;

namespace Dyss.Presentation.ViewModels;

/// <summary>One of the seven day buttons.</summary>
public sealed partial class DayOption(ScheduleDays day, string letter, string name) : ObservableObject
{
    public ScheduleDays Day { get; } = day;
    public string Letter { get; } = letter;
    public string Name { get; } = name;
    [ObservableProperty] private bool _isChecked;
}

/// <summary>
/// The schedule editor: start time, days, and the rooms with their settings, laid out like the
/// Nettoyage panel so a schedule reads the same as a clean started by hand. It only builds the
/// schedule; sending it is <see cref="SchedulesViewModel"/>'s job.
/// </summary>
public sealed partial class ScheduleEditorViewModel : ObservableObject
{
    private readonly CleaningSchedule? _existing;
    private readonly IReadOnlyList<CleaningSchedule> _others;
    private readonly double _minutesPerSquareMetre;
    private int _nextOrder = 1;

    public ScheduleEditorViewModel(MapItem map, CleaningSchedule? existing, IReadOnlyList<CleaningSchedule> others, double minutesPerSquareMetre)
    {
        _existing = existing;
        _others = others;
        _minutesPerSquareMetre = minutesPerSquareMetre;
        Map = map;
        Title = existing is null ? "Nouvel horaire" : "Modifier l'horaire";
        _hour = existing?.Hour ?? 10;
        _minute = existing?.Minute ?? 0;

        Days = [.. ScheduleDayLabels.All.Select(d => new DayOption(d.Day, d.Letter, d.Long) { IsChecked = existing?.Days.HasFlag(d.Day) == true })];
        foreach (var d in Days) d.PropertyChanged += (_, _) => Recompute();

        var chosen = existing?.Rooms.Select((r, i) => (r, i)).ToDictionary(x => x.r.ZoneId.ToString(CultureInfo.InvariantCulture), x => (x.r.Settings, Order: x.i + 1))
                     ?? [];
        // The same order as the Nettoyage panel, so a room is found where one expects it.
        foreach (var z in (map.Metadata.Zones ?? []).OrderBy(z => z.Name, StringComparer.Create(new CultureInfo("fr-FR"), ignoreCase: true)))
        {
            var item = new ZoneItem(z);
            if (chosen.TryGetValue(item.Id, out var c))
            {
                item.Apply(c.Settings);
                item.Selected = true;
                item.Order = c.Order;
                item.IsExpanded = false;
                _nextOrder = Math.Max(_nextOrder, c.Order + 1);
            }
            item.PropertyChanged += OnRoomChanged;
            Rooms.Add(item);
        }
        Recompute();
    }

    public MapItem Map { get; }
    public string Title { get; }
    public string MapText => $"Pour la carte « {Map.Metadata.Name ?? Map.Id} » : un horaire appartient à une carte et ne s'exécute que lorsqu'elle est active.";

    public IReadOnlyList<int> Hours { get; } = Enumerable.Range(0, 24).ToList();
    public IReadOnlyList<int> Minutes { get; } = Enumerable.Range(0, 60).ToList();
    [ObservableProperty] private int _hour;
    [ObservableProperty] private int _minute;

    public ObservableCollection<DayOption> Days { get; }
    public ObservableCollection<ZoneItem> Rooms { get; } = new();

    /// <summary>At least one day and one room: the phone refuses the same, and the robot has nothing else to go on.</summary>
    [ObservableProperty] private bool _canSave;
    /// <summary>What is still missing, shown beside the disabled button.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(MissingTip))] private string _missing = "";
    /// <summary>The same, or null once nothing is missing: an empty tooltip would still show as an empty bubble.</summary>
    public string? MissingTip => Missing == "" ? null : Missing;
    [ObservableProperty] private string _estimateText = "";
    /// <summary>Another schedule of this map due while this one runs, or running when this one is due.</summary>
    [ObservableProperty] private string _overlapWarning = "";

    partial void OnHourChanged(int value) => Recompute();
    partial void OnMinuteChanged(int value) => Recompute();

    private void OnRoomChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not ZoneItem item) return;
        if (e.PropertyName == nameof(ZoneItem.Selected))
        {
            if (item.Selected) item.Order = _nextOrder++;
            else
            {
                // Close the gap so the numbers stay 1, 2, 3…
                var removed = item.Order;
                item.Order = 0;
                foreach (var z in Rooms.Where(z => z.Selected && z.Order > removed)) z.Order--;
                _nextOrder = Math.Max(1, _nextOrder - 1);
            }
        }
        if (e.PropertyName is nameof(ZoneItem.Selected) or nameof(ZoneItem.SelectedCleanType) or nameof(ZoneItem.SelectedMopPasses))
            Recompute();
        if (e.PropertyName == nameof(ZoneItem.Selected)) ClearRoomsCommand.NotifyCanExecuteChanged();
    }

    private bool AnyRoomTicked() => Rooms.Any(r => r.Selected);

    /// <summary>Unticks every room, to start the choice over.</summary>
    [RelayCommand(CanExecute = nameof(AnyRoomTicked))]
    private void ClearRooms()
    {
        // Highest order first, so each untick closes no gap before the next.
        foreach (var r in Rooms.Where(r => r.Selected).OrderByDescending(r => r.Order).ToList()) r.Selected = false;
    }

    private ScheduleDays SelectedDays => Days.Where(d => d.IsChecked).Aggregate(ScheduleDays.None, (all, d) => all | d.Day);

    private void Recompute()
    {
        var days = SelectedDays;
        var rooms = Rooms.Where(r => r.Selected).ToList();
        Missing = (days == ScheduleDays.None, rooms.Count == 0) switch
        {
            (true, true) => "Choisissez au moins un jour et une pièce.",
            (true, false) => "Choisissez au moins un jour.",
            (false, true) => "Choisissez au moins une pièce.",
            _ => "",
        };
        CanSave = Missing == "";

        if (rooms.Count == 0)
        {
            EstimateText = "";
            OverlapWarning = "";
            return;
        }
        var minutes = CleanDurationEstimate.Minutes(rooms.Select(r => (r.Area, r.Settings)), _minutesPerSquareMetre);
        EstimateText = $"Durée estimée : environ {FormatMinutes(minutes)}, d'après les nettoyages passés.";

        OverlapWarning = "";
        if (days == ScheduleDays.None) return;
        var self = Build(_existing?.Id ?? 0);
        foreach (var other in _others.Where(o => o.Enabled && o.MapId == self.MapId && o.Id != self.Id))
        {
            var otherMinutes = CleanDurationEstimate.Minutes(other.Rooms.Select(r => (AreaOf(r.ZoneId), r.Settings)), _minutesPerSquareMetre);
            if (CleanDurationEstimate.Overlaps(other, otherMinutes, self) || CleanDurationEstimate.Overlaps(self, minutes, other))
            {
                OverlapWarning = $"Chevauche l'horaire de {ScheduleDayLabels.Time(other.Hour, other.Minute)} ({ScheduleDayLabels.Describe(other.Days)}) : "
                    + "le robot ne commence pas un horaire tant que le précédent est en cours.";
                return;
            }
        }
    }

    private double AreaOf(int zoneId) =>
        Rooms.FirstOrDefault(r => r.Id == zoneId.ToString(CultureInfo.InvariantCulture))?.Area ?? 0;

    internal static string FormatMinutes(int minutes) =>
        minutes < 60 ? $"{minutes} min" : $"{minutes / 60} h {minutes % 60:00}";

    /// <summary>The schedule as edited, under the given id; an edit keeps whether it was on.</summary>
    public CleaningSchedule Build(long id) => new(
        id,
        long.TryParse(Map.Id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var mapId) ? mapId : 0,
        _existing?.Enabled ?? true,
        SelectedDays,
        Hour,
        Minute,
        Rooms.Where(r => r.Selected).OrderBy(r => r.Order)
            .Where(r => int.TryParse(r.Id, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            .Select(r => new ScheduledRoom(int.Parse(r.Id, CultureInfo.InvariantCulture), r.Settings))
            .ToList());
}
