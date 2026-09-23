using System.Windows;
using MyDyson.App.ViewModels;

namespace MyDyson.App.Views;

public partial class MapManagerWindow : Window
{
    private readonly MapManagerViewModel _vm;

    /// <summary>Raised after an edit the dashboard should pick up (a rename changes what it lists).</summary>
    public event Action? MapsChanged;

    public MapManagerWindow(MapManagerViewModel vm, string? openOnMapId = null)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;

        // The prompts belong to the window, not the view model: what it needs is an answer, and
        // going through delegates keeps it free of MessageBox and testable without a screen.
        vm.AskForText = (title, prompt, initial) => TextPromptWindow.Ask(this, title, prompt, initial);
        vm.AskForRoomName = (room, types) => RoomNameWindow.Ask(this, room, types);
        vm.Confirm = (title, text) =>
            MessageBox.Show(this, text, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
        vm.Changed += () => MapsChanged?.Invoke();
        // The map view owns the picking gesture; the view model only says when it is on.
        vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MapManagerViewModel.Splitting)) MapCanvas.IsPickingLine = vm.Splitting;
        };

        MapCanvas.ZoneClicked += vm.RoomClickedById;
        MapCanvas.EmptySpaceClicked += vm.ClearRoomSelection;
        MapCanvas.LinePicked += (from, to) => _ = vm.SplitAsync(from, to);
        Loaded += async (_, _) => await vm.LoadAsync(openOnMapId);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>Diagnostics only: renders this window to a PNG, as MainWindow does for the dashboard.</summary>
    public void SaveScreenshot(string path) => WindowScreenshot.Save(this, path);
}
