using DyssCockpit.Core;
using DyssCockpit.Presentation.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using static DyssCockpit.Core.Translation;

namespace DyssCockpit.App.Views;

/// <summary>
/// Renaming a room: a type from the robot's own list, or a free name. Both travel the same way —
/// the wire field is always {"type": …, "name": …}, with type "custom" for a free name — so one
/// dialog covers what the phone app splits across two screens.
/// </summary>
public sealed partial class RoomNameDialog : ContentDialog
{
    private bool _nameEdited;

    public RoomNameDialog(ManagedRoom room, IReadOnlyList<RoomTypeOption> types)
    {
        InitializeComponent();
        Intro.Text = T($"Pièce « {room.DisplayName} », {room.AreaText}.", $"Room “{room.DisplayName}”, {room.AreaText}.");
        TypeBox.ItemsSource = types;
        TypeBox.SelectedItem = types.FirstOrDefault(t => t.Type == room.Zone.Type) ?? types[0];
        NameBox.Text = room.Zone.Name ?? room.DisplayName;
        // Once the name has been typed in, changing the type stops overwriting it.
        NameBox.TextChanged += (_, _) =>
        {
            _nameEdited = true;
            // An empty name is not something to send, and Enter on a blank field should not look
            // like a working action.
            IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(NameBox.Text);
        };
        Opened += (_, _) =>
        {
            NameBox.Focus(FocusState.Programmatic);
            NameBox.SelectAll();
            _nameEdited = false;
        };
    }

    /// <summary>The chosen name and type (null type meaning a free name), once the user has pressed "Renommer".</summary>
    public (string Name, string? Type) Answer => (NameBox.Text, (TypeBox.SelectedItem as RoomTypeOption)?.Type);

    private void Type_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (TypeBox.SelectedItem is not RoomTypeOption option) return;
        // A free name has no default to go back to, so the reset only means something with a type.
        ResetNameButton.IsEnabled = option.Type is not null;
        if (_nameEdited) return;
        if (RoomTypeLabels.DefaultNameFor(option.Type) is { } label) NameBox.Text = label;
        _nameEdited = false;
    }

    /// <summary>Puts back the name Dyson gives this type, for undoing a free name without retyping it.</summary>
    private void ResetName_Click(object sender, RoutedEventArgs e)
    {
        if (RoomTypeLabels.DefaultNameFor((TypeBox.SelectedItem as RoomTypeOption)?.Type) is not { } label) return;
        NameBox.Text = label;
        _nameEdited = false;
    }
}
