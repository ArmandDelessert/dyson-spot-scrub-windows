using DyssCockpit.App.Controls;
using DyssCockpit.Presentation.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DyssCockpit.App.Views;

/// <summary>The dashboard: the robot's state, the clean (rooms or a drawn zone), the dock, the consumables, and the live map.</summary>
public sealed partial class DashboardView : UserControl
{
    public DashboardView(MainViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
    }

    public MainViewModel ViewModel { get; }

    public MapView Map => MapCanvas;

    private void ResetZoom_Click(object sender, RoutedEventArgs e) => MapCanvas.ResetView();
}
