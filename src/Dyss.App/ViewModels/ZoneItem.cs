using CommunityToolkit.Mvvm.ComponentModel;
using Dyss.Core;

namespace Dyss.App.ViewModels;

public sealed record CleanTypeOption(CleanType Value, string Label);
public sealed record StrategyOption(CleaningStrategy Value, string Label);
public sealed record WaterLevelOption(WaterLevel Value, string Label);
public sealed record MopPassesOption(int Value, string Label);

public partial class ZoneItem : ObservableObject
{
    // Labels from the Android app's per-room editing screen (translated options improved: the
    // official French translation says "aspirez"/"aspirateur" inconsistently across the four
    // options; ours is uniform).
    public static readonly IReadOnlyList<CleanTypeOption> CleanTypeOptions =
    [
        new(CleanType.Vacuum, "Aspirer"),
        new(CleanType.Mop, "Laver"),
        new(CleanType.VacuumAndMop, "Aspirer et laver"),
        new(CleanType.VacuumThenMop, "Aspirer puis laver"),
    ];

    public static readonly IReadOnlyList<StrategyOption> StrategyOptions =
    [
        new(CleaningStrategy.Auto, "Auto"),
        new(CleaningStrategy.Quick, "Rapide"),
        new(CleaningStrategy.Quiet, "Silencieux"),
        new(CleaningStrategy.Boost, "Boost"),
    ];

    public static readonly IReadOnlyList<WaterLevelOption> WaterLevelOptions =
    [
        new(WaterLevel.Low, "Faible"),
        new(WaterLevel.Medium, "Moyen"),
        new(WaterLevel.High, "Élevé"),
    ];

    public static readonly IReadOnlyList<MopPassesOption> MopPassesOptions =
    [
        new(1, "1 x"),
        new(2, "2 x"),
    ];

    public string Id { get; }
    public string Name { get; }
    public string Type { get; }
    public double Area { get; }
    public ZoneMetadata Metadata { get; private set; }

    [ObservableProperty] private bool _selected;
    [ObservableProperty] private int _order;
    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private CleanTypeOption _selectedCleanType;
    [ObservableProperty] private StrategyOption _selectedStrategy;
    [ObservableProperty] private WaterLevelOption _selectedWaterLevel;
    [ObservableProperty] private MopPassesOption _selectedMopPasses;

    public string OrderText => Selected && Order > 0 ? $"{Order}." : "";

    /// <summary>What the phone app would show: the room type's own label, since it ignores the stored name for typed rooms.</summary>
    public string DisplayName => RoomTypeLabels.Resolve(string.IsNullOrEmpty(Type) ? null : Type, Name, Id);

    /// <summary>Whether the vacuum-power setting applies: every clean type except mop alone.</summary>
    public bool HasVacuum => SelectedCleanType.Value is CleanType.Vacuum or CleanType.VacuumAndMop or CleanType.VacuumThenMop;

    /// <summary>Whether the mop-only settings (water level, passes) apply to the current clean type.</summary>
    public bool HasMop => SelectedCleanType.Value is CleanType.Mop or CleanType.VacuumAndMop or CleanType.VacuumThenMop;

    /// <summary>One-line recap shown when the row is collapsed, in the phone app's own style.</summary>
    public string Summary
    {
        get
        {
            var parts = new List<string> { SelectedCleanType.Label };
            if (HasVacuum) parts.Add(SelectedStrategy.Label);
            if (HasMop) { parts.Add(SelectedWaterLevel.Label); parts.Add(SelectedMopPasses.Label); }
            return string.Join(" · ", parts);
        }
    }

    public ZoneItem(ZoneMetadata z)
    {
        Id = z.Id;
        Name = z.Name ?? z.Id;
        Type = z.Type ?? "";
        Area = z.Area ?? 0;
        Metadata = z;
        _selectedCleanType = CleanTypeOptions.First(o => o.Value == CleanTypes.FromRest(z.Settings?.CleanType));
        _selectedStrategy = StrategyOptions.First(o => o.Value == CleaningStrategies.FromRest(z.Settings?.CleaningStrategy));
        _selectedWaterLevel = WaterLevelOptions.First(o => o.Value == WaterLevels.FromRest(z.Settings?.WaterLevel));
        _selectedMopPasses = MopPassesOptions.FirstOrDefault(o => o.Value == z.Settings?.MopPasses) ?? MopPassesOptions[0];
    }

    /// <summary>The four choices as the robot takes them, for a clean or a schedule.</summary>
    public RoomSettings Settings => new(SelectedCleanType.Value, SelectedStrategy.Value, SelectedWaterLevel.Value, SelectedMopPasses.Value);

    /// <summary>Shows settings chosen elsewhere, such as a stored schedule's, instead of the map's own.</summary>
    public void Apply(RoomSettings s)
    {
        SelectedCleanType = CleanTypeOptions.First(o => o.Value == s.CleanType);
        SelectedStrategy = StrategyOptions.First(o => o.Value == s.Strategy);
        SelectedWaterLevel = WaterLevelOptions.First(o => o.Value == s.Water);
        SelectedMopPasses = MopPassesOptions.FirstOrDefault(o => o.Value == s.MopPasses) ?? MopPassesOptions[0];
    }

    /// <summary>The metadata entry with this item's current choices written back, for the REST PUT.</summary>
    public ZoneMetadata ToMetadata() => Metadata with
    {
        IsSelected = Selected,
        Order = Selected ? Order : 0,
        Settings = (Metadata.Settings ?? new ZoneSettings("auto", null, "low", 1, 1, true)) with
        {
            CleanType = SelectedCleanType.Value.ToRest(),
            CleaningStrategy = SelectedStrategy.Value.ToRest(),
            WaterLevel = SelectedWaterLevel.Value.ToRest(),
            MopPasses = SelectedMopPasses.Value,
        },
    };

    partial void OnSelectedChanged(bool value)
    {
        OnPropertyChanged(nameof(OrderText));
        // Expand a room's settings as soon as it is picked, collapse them again once removed;
        // the manual toggle still works independently, e.g. to peek at an unselected room.
        IsExpanded = value;
    }

    partial void OnOrderChanged(int value) => OnPropertyChanged(nameof(OrderText));

    partial void OnSelectedCleanTypeChanged(CleanTypeOption value)
    {
        OnPropertyChanged(nameof(HasVacuum));
        OnPropertyChanged(nameof(HasMop));
        OnPropertyChanged(nameof(Summary));
    }

    partial void OnSelectedStrategyChanged(StrategyOption value) => OnPropertyChanged(nameof(Summary));
    partial void OnSelectedWaterLevelChanged(WaterLevelOption value) => OnPropertyChanged(nameof(Summary));
    partial void OnSelectedMopPassesChanged(MopPassesOption value) => OnPropertyChanged(nameof(Summary));
}
