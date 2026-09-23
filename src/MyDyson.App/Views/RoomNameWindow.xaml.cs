using System.Windows;
using System.Windows.Controls;
using MyDyson.App.ViewModels;
using MyDyson.Core;

namespace MyDyson.App.Views;

/// <summary>
/// Renaming a room: a type from the robot's own list, or a free name. Both travel the same way —
/// the wire field is always {"type": …, "name": …}, with type "custom" for a free name — so one
/// dialog covers what the phone app splits across two screens.
/// </summary>
public partial class RoomNameWindow : Window
{
    private bool _nameEdited;

    private RoomNameWindow(ManagedRoom room, IReadOnlyList<RoomTypeOption> types)
    {
        InitializeComponent();
        Intro.Text = $"Pièce « {room.DisplayName} », {room.AreaText}.";
        TypeBox.ItemsSource = types;
        TypeBox.SelectedItem = types.FirstOrDefault(t => t.Type == room.Zone.Type) ?? types[0];
        NameBox.Text = room.Zone.Name ?? room.DisplayName;
        // Once the name has been typed in, changing the type stops overwriting it.
        NameBox.TextChanged += (_, _) => _nameEdited = true;
        Loaded += (_, _) => { NameBox.Focus(); NameBox.SelectAll(); _nameEdited = false; };
    }

    /// <summary>Returns the chosen name and type (null type meaning a free name), or null when cancelled.</summary>
    public static (string Name, string? Type)? Ask(Window owner, ManagedRoom room, IReadOnlyList<RoomTypeOption> types)
    {
        var w = new RoomNameWindow(room, types) { Owner = owner };
        if (w.ShowDialog() != true) return null;
        return (w.NameBox.Text, (w.TypeBox.SelectedItem as RoomTypeOption)?.Type);
    }

    private void Type_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_nameEdited || TypeBox.SelectedItem is not RoomTypeOption option) return;
        if (RoomTypeLabels.DefaultNameFor(option.Type) is { } label) NameBox.Text = label;
        _nameEdited = false;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(NameBox.Text)) { NameBox.Focus(); return; }
        DialogResult = true;
    }
}
