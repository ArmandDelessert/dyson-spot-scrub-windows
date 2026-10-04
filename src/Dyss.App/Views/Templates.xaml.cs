using Dyss.Presentation.ViewModels;
using Microsoft.UI.Xaml;

namespace Dyss.App.Views;

/// <summary>The data templates shared by several views; a class of their own so they can use compiled bindings.</summary>
public sealed partial class Templates : ResourceDictionary
{
    public Templates() => InitializeComponent();

    /// <summary>Folds or unfolds a room's settings.</summary>
    private void Expand_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ZoneItem room) room.IsExpanded = !room.IsExpanded;
    }
}
