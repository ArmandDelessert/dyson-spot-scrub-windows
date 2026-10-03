using System.Windows;
using System.Windows.Input;
using Dyss.Presentation.ViewModels;

namespace Dyss.App.Views;

public partial class MapManagerWindow : Window
{
    /// <summary>Raised after an edit the dashboard should pick up (a rename changes what it lists).</summary>
    public event Action? MapsChanged;

    public MapManagerWindow(MapManagerViewModel vm, string? openOnMapId = null)
    {
        InitializeComponent();
        DataContext = vm;
        vm.Changed += () => MapsChanged?.Invoke();
        // Escape puts back a zone or a piece being dragged before it means leaving a mode.
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && MapCanvas.CancelGesture()) e.Handled = true;
        };
        Loaded += async (_, _) => await vm.LoadAsync(openOnMapId);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>Diagnostics only: renders this window to a PNG, as MainWindow does for the dashboard.</summary>
    public void SaveScreenshot(string path) => WindowScreenshot.Save(this, path);
}
