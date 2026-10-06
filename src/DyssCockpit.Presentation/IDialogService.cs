using DyssCockpit.Presentation.Map;
using DyssCockpit.Presentation.ViewModels;

namespace DyssCockpit.Presentation;

/// <summary>
/// The questions the view models put to the user, answered by the UI that hosts them. Everything is
/// asynchronous, as WinUI's content dialogs and file pickers are awaited.
/// </summary>
public interface IDialogService
{
    /// <summary>A yes/no question; true for yes.</summary>
    Task<bool> ConfirmAsync(string title, string message);

    /// <summary>A warning the user only has to acknowledge.</summary>
    Task AlertAsync(string title, string message);

    /// <summary>A one-line text, pre-filled with <paramref name="initial"/>; null when the user cancels.</summary>
    Task<string?> AskTextAsync(string title, string prompt, string initial);

    /// <summary>A room's new name and type (null type for a free name); null when the user cancels.</summary>
    Task<(string Name, string? Type)?> AskRoomNameAsync(ManagedRoom room, IReadOnlyList<RoomTypeOption> types);

    /// <summary>Shows the schedule editor; true when the user saves, false when they cancel.</summary>
    Task<bool> EditScheduleAsync(ScheduleEditorViewModel editor);

    /// <summary>
    /// Asks where to save <paramref name="scene"/> as a PNG image and writes it there. Drawing is the
    /// UI's business, so this does both. Returns the file written, or null when the user cancels.
    /// </summary>
    Task<string?> SaveMapImageAsync(MapScene scene, string suggestedFileName);
}
