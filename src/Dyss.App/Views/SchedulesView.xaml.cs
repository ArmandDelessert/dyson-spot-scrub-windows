using Dyss.Presentation.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Dyss.App.Views;

/// <summary>The active map's schedules: listed, switched on and off, created, edited, deleted.</summary>
public sealed partial class SchedulesView : UserControl
{
    public SchedulesView(SchedulesViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
    }

    public SchedulesViewModel ViewModel { get; }

    private static ScheduleItem? ItemOf(object sender) => (sender as FrameworkElement)?.Tag as ScheduleItem;

    /// <summary>
    /// Sends the change only when the switch disagrees with the schedule, and puts the switch back
    /// when nothing can be sent (robot offline, another change under way). The list is rebuilt
    /// from the robot's answer either way, so a refused change shows as it really is.
    /// </summary>
    private void Toggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleSwitch { Tag: ScheduleItem item } toggle || toggle.IsOn == item.Enabled) return;
        if (ViewModel.ToggleCommand.CanExecute(item)) ViewModel.ToggleCommand.Execute(item);
        else toggle.IsOn = item.Enabled;
    }

    private void Edit_Click(object sender, RoutedEventArgs e) => ViewModel.EditCommand.Execute(ItemOf(sender));

    private void Delete_Click(object sender, RoutedEventArgs e) => ViewModel.DeleteCommand.Execute(ItemOf(sender));
}
