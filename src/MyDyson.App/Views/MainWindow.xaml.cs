using System.Windows;
using MyDyson.App.Services;
using MyDyson.App.ViewModels;

namespace MyDyson.App.Views;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "A Window's lifetime is its own Closed event, which is where both fields are disposed; making the window IDisposable would suggest a second, competing lifetime.")]
public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly NotificationService _notifications = new();

    /// <summary>Raised when the user logs out from within the window, just before it closes.</summary>
    public event Action? LoggedOut;

    public MainWindow(RobotContext ctx)
    {
        InitializeComponent();
        _vm = new MainViewModel(ctx);
        DataContext = _vm;
        MapCanvas.ZoneClicked += _vm.Cleaning.ToggleZone;
        MapCanvas.EmptySpaceClicked += _vm.Cleaning.ClearSelection;
        _vm.LoggedOut += () => { LoggedOut?.Invoke(); Close(); };
        _vm.NotifyRequested += (title, text) => _notifications.Show(title, text, onClick: () =>
        {
            Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
            SelectTab(1);
        });
        _vm.MapManagerRequested += OpenMapManager;
        _vm.Schedules.EditSchedule = editor => ScheduleEditorWindow.Ask(this, editor);
        _vm.Schedules.Confirm = (title, text) =>
            MessageBox.Show(this, text, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
        Loaded += async (_, _) => await _vm.StartAsync();
        Closing += async (_, _) => await _vm.ShutdownAsync();
        Closed += (_, _) => { _notifications.Dispose(); _vm.Dispose(); };
    }

    /// <summary>Opens the map manager, and refreshes the dashboard afterwards so renames show up there too.</summary>
    private void OpenMapManager()
    {
        var window = new MapManagerWindow(_vm.CreateMapManager(), _vm.Cleaning.SelectedMap?.Id) { Owner = this };
        window.MapsChanged += () => _ = _vm.ReloadMapsAsync();
        window.ShowDialog();
    }

    private void ResetZoom_Click(object sender, RoutedEventArgs e) => MapCanvas.ResetView();
    private void ResetHistoryZoom_Click(object sender, RoutedEventArgs e) => HistoryCanvas.ResetView();

    public void ClickZone(string zoneId) => _vm.Cleaning.ToggleZone(zoneId);

    public void SelectTab(int index)
    {
        if (index >= 0 && index < Tabs.Items.Count) Tabs.SelectedIndex = index;
        if (index == 1) _vm.History.SelectFirstClean();
    }

    private MapManagerWindow? _mapManager;

    /// <summary>Diagnostics only: opens the map manager without waiting for it, so a screenshot run can shoot it.</summary>
    public void OpenMapManagerForScreenshot(int layer = 0, string? mapId = null)
    {
        var vm = _vm.CreateMapManager();
        vm.LayerIndex = layer;
        _mapManager = new MapManagerWindow(vm, mapId ?? _vm.Cleaning.SelectedMap?.Id) { Owner = this };
        _mapManager.Show();
    }

    /// <summary>Diagnostics only: renders the map manager to a PNG.</summary>
    public void SaveMapManagerScreenshot(string path) => _mapManager?.SaveScreenshot(path);

    private ScheduleEditorWindow? _scheduleEditor;

    /// <summary>
    /// Diagnostics only: opens the editor of a new schedule, already filled in (Monday and Thursday,
    /// the first two rooms) so the shot shows the estimate and the settings rows.
    /// </summary>
    public void OpenScheduleEditorForScreenshot()
    {
        if (_vm.Schedules.NewEditor() is not { } editor) return;
        editor.Days[0].IsChecked = true;
        editor.Days[3].IsChecked = true;
        foreach (var room in editor.Rooms.Take(2)) room.Selected = true;
        _scheduleEditor = ScheduleEditorWindow.ShowForScreenshot(this, editor);
    }

    public void SaveScheduleEditorScreenshot(string path) => _scheduleEditor?.SaveScreenshot(path);

    /// <summary>Renders the window content to a PNG, for documentation and for checking the layout without a screen.</summary>
    public void SaveScreenshot(string path) => WindowScreenshot.Save(this, path);
}
