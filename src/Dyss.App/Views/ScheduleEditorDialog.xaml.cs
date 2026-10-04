using System.Globalization;
using Dyss.Presentation.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace Dyss.App.Views;

/// <summary>Creating or changing a schedule. The view model does the work; this only shows it and says OK.</summary>
public sealed partial class ScheduleEditorDialog : ContentDialog
{
    public ScheduleEditorDialog(ScheduleEditorViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        // Hours and minutes on two digits, as a clock shows them; the index is the value.
        HourBox.ItemsSource = viewModel.Hours.Select(h => h.ToString("00", CultureInfo.InvariantCulture)).ToList();
        MinuteBox.ItemsSource = viewModel.Minutes.Select(m => m.ToString("00", CultureInfo.InvariantCulture)).ToList();
        HourBox.SelectedIndex = viewModel.Hour;
        MinuteBox.SelectedIndex = viewModel.Minute;
    }

    public ScheduleEditorViewModel ViewModel { get; }
}
