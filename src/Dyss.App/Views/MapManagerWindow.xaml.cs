using System.Windows;
using System.Windows.Input;
using Dyss.App.Controls;
using Dyss.App.ViewModels;

namespace Dyss.App.Views;

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
        // The map view owns the picking gestures; the view model only says which one is on.
        vm.PropertyChanged += (_, args) =>
        {
            switch (args.PropertyName)
            {
                case nameof(MapManagerViewModel.Splitting) or nameof(MapManagerViewModel.AddingZone)
                    or nameof(MapManagerViewModel.PlacingFurniture):
                    MapCanvas.Picking = vm.Splitting ? MapPick.Line
                        : vm.AddingZone ? MapPick.Rectangle
                        : vm.PlacingFurniture ? MapPick.Point
                        : MapPick.None;
                    break;
                case nameof(MapManagerViewModel.PlacementShape):
                    MapCanvas.PlacementShape = vm.PlacementShape;
                    break;
                case nameof(MapManagerViewModel.GridActive):
                    MapCanvas.SnapToGrid = vm.GridActive;
                    break;
                // Raised together, on every change of choice, tab or state that could affect them.
                case nameof(MapManagerViewModel.EditableShape):
                    if (!ReferenceEquals(MapCanvas.EditableShape, vm.EditableShape)) MapCanvas.EditableShape = vm.EditableShape;
                    MapCanvas.EditableShapeResizable = vm.EditableShapeResizable;
                    break;
            }
        };
        MapCanvas.ShapeEdited += corners => _ = vm.ShapeDroppedAsync(corners);
        // Escape puts back a zone or a piece being dragged before it means leaving a mode.
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && MapCanvas.CancelGesture()) e.Handled = true;
        };

        // Every click goes to the view model, which knows which tab it is for.
        MapCanvas.WorldClicked += vm.MapClickedAt;
        MapCanvas.ZoneClicked += vm.RoomClickedById;
        // Clearing here costs nothing, so it need not wait to see whether a double click follows.
        MapCanvas.DeferEmptySpaceClick = false;
        // Zones, drawn or resized, never shrink below what the view model accepts.
        MapCanvas.MinimumShapeSide = MapManagerViewModel.MinimumZoneSide;
        // Cuts, and zones and furniture when the box says so, land on the robot's 5 cm grid.
        MapCanvas.SnapToGrid = vm.GridActive;
        MapCanvas.EmptySpaceClicked += vm.ClearRoomSelection;
        MapCanvas.LinePicked += (from, to) => _ = vm.SplitAsync(from, to);
        MapCanvas.RectanglePicked += (a, b) => _ = vm.ZoneDrawnAsync(a, b);
        MapCanvas.PointPicked += p => _ = vm.FurniturePointPickedAsync(p);
        Loaded += async (_, _) => await vm.LoadAsync(openOnMapId);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>Diagnostics only: renders this window to a PNG, as MainWindow does for the dashboard.</summary>
    public void SaveScreenshot(string path) => WindowScreenshot.Save(this, path);
}
