using DyssCockpit.Presentation.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace DyssCockpit.App.Views;

/// <summary>The application's own settings: notifications, behavior, language, the map's display, and the version.</summary>
public sealed partial class SettingsView : UserControl
{
    public SettingsView(SettingsViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
    }

    public SettingsViewModel ViewModel { get; }
}
