using Dyss.Presentation.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Dyss.App.Views;

/// <summary>Past cleans, and the chosen one's trail on its map.</summary>
public sealed partial class HistoryView : UserControl
{
    public HistoryView(HistoryViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
    }

    public HistoryViewModel ViewModel { get; }

    private void ResetZoom_Click(object sender, RoutedEventArgs e) => MapCanvas.ResetView();
}
