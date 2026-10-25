using CommunityToolkit.Mvvm.ComponentModel;
using DyssCockpit.Core;

using static DyssCockpit.Core.Translation;

namespace DyssCockpit.Presentation.ViewModels;

/// <summary>
/// The settings the ticked rooms share. Choosing a clean type, a suction mode, a water level or a
/// number of passes here sets it on every ticked room it applies to, at once; a room can still be
/// set on its own with its arrow. Where the ticked rooms differ on one of the four, its list shows
/// "Varied" rather than a value, and picking one makes them the same: the panel always says what
/// the rooms really are, and a room ticked after the choice keeps its own settings until it is made
/// to follow.
/// </summary>
public sealed partial class CleaningViewModel
{
    /// <summary>Set while the four lists are being brought up to date, so that this is not taken for a choice.</summary>
    private bool _refreshingCommon;

    /// <summary>Set while a choice is being written to the rooms, whose own changes each ask for a refresh.</summary>
    private bool _applyingCommon;

    /// <summary>How many rooms are ticked.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasTickedRooms)), NotifyPropertyChangedFor(nameof(CommonTitle))]
    private int _tickedRooms;

    /// <summary>The clean type of the ticked rooms; null when they differ.</summary>
    [ObservableProperty] private CleanTypeOption? _commonCleanType;

    /// <summary>The suction mode of the ticked rooms that vacuum; null when they differ.</summary>
    [ObservableProperty] private StrategyOption? _commonStrategy;

    /// <summary>The water level of the ticked rooms that mop; null when they differ.</summary>
    [ObservableProperty] private WaterLevelOption? _commonWaterLevel;

    /// <summary>The number of mopping passes of the ticked rooms that mop; null when they differ.</summary>
    [ObservableProperty] private MopPassesOption? _commonMopPasses;

    /// <summary>Whether one of the ticked rooms vacuums, so that a suction mode means something.</summary>
    [ObservableProperty] private bool _commonHasVacuum;

    /// <summary>Whether one of the ticked rooms mops, so that a water level and passes mean something.</summary>
    [ObservableProperty] private bool _commonHasMop;

    /// <summary>Rooms are ticked, and not set aside by a drawn zone: the panel has something to set.</summary>
    public bool HasTickedRooms => TickedRooms > 0;

    public string CommonTitle => TickedRooms == 1 ? T("Cleaning_CommonSettingsOne") : Tf("Cleaning_CommonSettingsMany", TickedRooms);

    /// <summary>What a list shows while the rooms differ on it.</summary>
    public static string MixedText => T("Cleaning_Mixed");

    partial void OnCommonCleanTypeChanged(CleanTypeOption? value) => ApplyCommon(value, (z, v) => z.SelectedCleanType = v, _ => true);
    partial void OnCommonStrategyChanged(StrategyOption? value) => ApplyCommon(value, (z, v) => z.SelectedStrategy = v, z => z.HasVacuum);
    partial void OnCommonWaterLevelChanged(WaterLevelOption? value) => ApplyCommon(value, (z, v) => z.SelectedWaterLevel = v, z => z.HasMop);
    partial void OnCommonMopPassesChanged(MopPassesOption? value) => ApplyCommon(value, (z, v) => z.SelectedMopPasses = v, z => z.HasMop);

    /// <summary>
    /// Writes a choice to the ticked rooms it applies to: a suction mode does not mean anything to a
    /// room that only mops, and writing it there would change what the cloud keeps for that room.
    /// </summary>
    private void ApplyCommon<T>(T? choice, Action<ZoneItem, T> set, Func<ZoneItem, bool> applies) where T : class
    {
        if (_refreshingCommon || choice is null) return;
        _applyingCommon = true;
        try
        {
            foreach (var room in Zones.Where(z => z.Selected && applies(z))) set(room, choice);
        }
        finally { _applyingCommon = false; }
        RefreshCommon();
    }

    /// <summary>Brings the four lists, and the panel around them, up to date with the ticked rooms.</summary>
    private void RefreshCommon()
    {
        if (_applyingCommon) return;
        var ticked = Zones.Where(z => z.Selected).ToList();
        _refreshingCommon = true;
        try
        {
            TickedRooms = ticked.Count;
            CommonHasVacuum = ticked.Any(z => z.HasVacuum);
            CommonHasMop = ticked.Any(z => z.HasMop);
            CommonCleanType = Shared(ticked, z => z.SelectedCleanType);
            CommonStrategy = Shared(ticked.Where(z => z.HasVacuum), z => z.SelectedStrategy);
            CommonWaterLevel = Shared(ticked.Where(z => z.HasMop), z => z.SelectedWaterLevel);
            CommonMopPasses = Shared(ticked.Where(z => z.HasMop), z => z.SelectedMopPasses);
        }
        finally { _refreshingCommon = false; }
    }

    /// <summary>What every one of the rooms has, or null when they differ (or there is none).</summary>
    private static T? Shared<T>(IEnumerable<ZoneItem> rooms, Func<ZoneItem, T> pick) where T : class
    {
        T? shared = null;
        foreach (var room in rooms)
        {
            var value = pick(room);
            if (shared is null) shared = value;
            else if (!shared.Equals(value)) return null;
        }
        return shared;
    }
}
