using System.Windows;
using Dyss.App.Rendering;
using Dyss.App.Views;
using Dyss.Presentation;
using Dyss.Presentation.Map;
using Dyss.Presentation.ViewModels;

namespace Dyss.App.Services;

/// <summary>
/// The view models' questions as WPF asks them: modal windows over whichever window the user is
/// working in — the dashboard, or the map manager over it. WPF's dialogs answer synchronously, so
/// every task comes back already completed.
/// </summary>
internal sealed class WpfDialogService : IDialogService
{
    /// <summary>The active window, or the main one when none is (the app in the background).</summary>
    private static Window? Owner =>
        Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? Application.Current.MainWindow;

    public Task<bool> ConfirmAsync(string title, string message) =>
        Task.FromResult(Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes);

    public Task AlertAsync(string title, string message)
    {
        Show(message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
        return Task.CompletedTask;
    }

    public Task<string?> AskTextAsync(string title, string prompt, string initial) =>
        Task.FromResult(TextPromptWindow.Ask(Owner, title, prompt, initial));

    public Task<(string Name, string? Type)?> AskRoomNameAsync(ManagedRoom room, IReadOnlyList<RoomTypeOption> types) =>
        Task.FromResult(RoomNameWindow.Ask(Owner, room, types));

    public Task<bool> EditScheduleAsync(ScheduleEditorViewModel editor) =>
        Task.FromResult(ScheduleEditorWindow.Ask(Owner, editor));

    public Task<string?> SaveMapImageAsync(MapScene scene, string suggestedFileName)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "Image PNG|*.png", FileName = suggestedFileName };
        if (dialog.ShowDialog() != true) return Task.FromResult<string?>(null);
        MapRenderer.ExportPng(scene, 1200, 1400, dialog.FileName);
        return Task.FromResult<string?>(dialog.FileName);
    }

    private static MessageBoxResult Show(string message, string title, MessageBoxButton buttons, MessageBoxImage image) =>
        Owner is { } owner
            ? MessageBox.Show(owner, message, title, buttons, image)
            : MessageBox.Show(message, title, buttons, image);
}
