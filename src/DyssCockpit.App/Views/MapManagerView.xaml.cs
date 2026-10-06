using DyssCockpit.App.Controls;
using DyssCockpit.Presentation.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace DyssCockpit.App.Views;

/// <summary>
/// The map manager: rename, delete or activate a map, rename, merge or split its rooms, edit its
/// restriction zones and furniture, run a new mapping scan. Shown in place of the dashboard's
/// pages; the view model does the work and the map view the gestures.
/// </summary>
public sealed partial class MapManagerView : UserControl
{
    public MapManagerView(MapManagerViewModel viewModel, string? openOnMapId)
    {
        ViewModel = viewModel;
        InitializeComponent();
        Layers.SelectedItem = Layers.Items[Math.Clamp(viewModel.LayerIndex, 0, Layers.Items.Count - 1)];
        Loaded += async (_, _) =>
        {
            if (_loaded) return;
            _loaded = true;
            await viewModel.LoadAsync(openOnMapId);
        };
    }

    private bool _loaded;

    public MapManagerViewModel ViewModel { get; }

    public MapView Map => MapCanvas;

    private void Layers_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (sender.SelectedItem is { } item) ViewModel.LayerIndex = sender.Items.IndexOf(item);
    }

    private void Maps_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (ViewModel.SetActiveCommand.CanExecute(null)) ViewModel.SetActiveCommand.Execute(null);
    }

    private void Room_Click(object sender, ItemClickEventArgs e) => ViewModel.ClickRoomCommand.Execute(e.ClickedItem as ManagedRoom);
    private void Zone_Click(object sender, ItemClickEventArgs e) => ViewModel.ClickZoneCommand.Execute(e.ClickedItem as ManagedZone);
    private void Furniture_Click(object sender, ItemClickEventArgs e) => ViewModel.ClickFurnitureCommand.Execute(e.ClickedItem as ManagedFurniture);

    /// <summary>
    /// Escape: puts back a zone or a piece being dragged, or else leaves the mode under way
    /// (splitting, merging, drawing a zone, placing a piece).
    /// </summary>
    public bool Cancel()
    {
        if (MapCanvas.CancelGesture()) return true;
        if (!ViewModel.InMode) return false;
        ViewModel.CancelCommand.Execute(null);
        return true;
    }
}
