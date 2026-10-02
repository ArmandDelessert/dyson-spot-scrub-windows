using System.Windows;
using Dyss.App.ViewModels;

namespace Dyss.App.Views;

/// <summary>Creating or changing a schedule. The view model does the work; this only shows it and says OK.</summary>
public partial class ScheduleEditorWindow : Window
{
    private readonly ScheduleEditorViewModel _vm;

    private ScheduleEditorWindow(ScheduleEditorViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
    }

    public static bool Ask(Window owner, ScheduleEditorViewModel vm) =>
        new ScheduleEditorWindow(vm) { Owner = owner }.ShowDialog() == true;

    /// <summary>Diagnostics only: shows the editor without waiting for it, so a screenshot run can shoot it.</summary>
    public static ScheduleEditorWindow ShowForScreenshot(Window owner, ScheduleEditorViewModel vm)
    {
        var w = new ScheduleEditorWindow(vm) { Owner = owner };
        w.Show();
        return w;
    }

    /// <summary>Diagnostics only: renders this window to a PNG.</summary>
    public void SaveScreenshot(string path) => WindowScreenshot.Save(this, path);

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        // Enter reaches the default button even while it is disabled in some layouts; the view model decides.
        if (_vm.CanSave) DialogResult = true;
    }
}
