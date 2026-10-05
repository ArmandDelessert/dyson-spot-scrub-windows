using Dyss.App.Rendering;
using Dyss.App.Views;
using Dyss.Presentation;
using Dyss.Presentation.Map;
using Dyss.Presentation.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;

using static Dyss.Core.Translation;

namespace Dyss.App.Services;

/// <summary>
/// The view models' questions as WinUI asks them: content dialogs over the window, in its theme,
/// and the system's save picker.
/// </summary>
internal sealed class WinUiDialogService(MainWindow window) : IDialogService
{
    /// <summary>Puts a dialog on the window, in the window's theme and with the system's own look.</summary>
    internal ContentDialog Prepare(ContentDialog dialog)
    {
        dialog.XamlRoot = window.Content.XamlRoot;
        dialog.RequestedTheme = window.Theme;
        dialog.Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"];
        return dialog;
    }

    private static TextBlock Text(string message) => new() { Text = message, TextWrapping = TextWrapping.Wrap };

    public async Task<bool> ConfirmAsync(string title, string message)
    {
        // The window may be in the notification area, or never shown yet: a session that
        // expired there must still be told.
        await window.EnsureShownAsync();
        var dialog = Prepare(new ContentDialog
        {
            Title = title,
            Content = Text(message),
            PrimaryButtonText = T("Oui", "Yes"),
            CloseButtonText = T("Non", "No"),
            DefaultButton = ContentDialogButton.Primary,
        });
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    public async Task AlertAsync(string title, string message)
    {
        await window.EnsureShownAsync();
        var dialog = Prepare(new ContentDialog
        {
            Title = title,
            Content = Text(message),
            CloseButtonText = "OK",
            DefaultButton = ContentDialogButton.Close,
        });
        await dialog.ShowAsync();
    }

    public async Task<string?> AskTextAsync(string title, string prompt, string initial)
    {
        await window.EnsureShownAsync();
        var input = new TextBox { Header = prompt, Text = initial };
        var dialog = Prepare(new ContentDialog
        {
            Title = title,
            Content = input,
            PrimaryButtonText = T("Valider", "OK"),
            CloseButtonText = T("Annuler", "Cancel"),
            DefaultButton = ContentDialogButton.Primary,
            // An empty name is not something the robot would take, and Enter on a blank field
            // should not look like a working action.
            IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(initial),
        });
        input.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(input.Text);
        input.Loaded += (_, _) => { input.Focus(FocusState.Programmatic); input.SelectAll(); };
        return await dialog.ShowAsync() == ContentDialogResult.Primary ? input.Text : null;
    }

    public async Task<(string Name, string? Type)?> AskRoomNameAsync(ManagedRoom room, IReadOnlyList<RoomTypeOption> types)
    {
        await window.EnsureShownAsync();
        var dialog = new RoomNameDialog(room, types);
        Prepare(dialog);
        return await dialog.ShowAsync() == ContentDialogResult.Primary ? dialog.Answer : null;
    }

    public async Task<bool> EditScheduleAsync(ScheduleEditorViewModel editor)
    {
        await window.EnsureShownAsync();
        var dialog = new ScheduleEditorDialog(editor);
        Prepare(dialog);
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    public async Task<string?> SaveMapImageAsync(MapScene scene, string suggestedFileName)
    {
        var picker = new FileSavePicker(window.AppWindow.Id)
        {
            SuggestedFileName = suggestedFileName,
            DefaultFileExtension = ".png",
        };
        picker.FileTypeChoices.Add(T("Image PNG", "PNG image"), [".png"]);
        if (await picker.PickSaveFileAsync() is not { } file) return null;
        await MapImage.ExportPngAsync(scene, 1200, 1400, file.Path, window.Theme == ElementTheme.Light ? MapPalette.Light : MapPalette.Dark);
        return file.Path;
    }
}
