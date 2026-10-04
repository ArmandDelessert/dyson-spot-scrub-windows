using System.ComponentModel;
using Dyss.Presentation.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace Dyss.App.Views;

/// <summary>The robot's settings, with the official app's wording; each one is sent as soon as it changes.</summary>
public sealed partial class SettingsView : UserControl, INotifyPropertyChanged
{
    public SettingsView(SettingsViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        // The slider works in doubles, the robot's volume in whole steps.
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SettingsViewModel.Volume)) PropertyChanged?.Invoke(this, new(nameof(VolumeValue)));
        };
    }

    public SettingsViewModel ViewModel { get; }

    public event PropertyChangedEventHandler? PropertyChanged;

    public double VolumeValue
    {
        get => ViewModel.Volume;
        set => ViewModel.Volume = (int)Math.Round(value);
    }
}
