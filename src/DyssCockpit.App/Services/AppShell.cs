using System.Diagnostics;
using DyssCockpit.Core;
using DyssCockpit.Presentation;
using DyssCockpit.Presentation.Services;
using Microsoft.Extensions.Logging;

namespace DyssCockpit.App.Services;

/// <summary>
/// What the settings page asks of the application around it (see <see cref="IAppShell"/>): the
/// notification service, the registry entry that starts the application with Windows, the restart
/// of the process, and the folder of its files.
/// </summary>
internal sealed partial class AppShell(NotificationService notifications, Func<string?> restart, Action showWindow, ILogger<AppShell> logger) : IAppShell
{
    public bool StartsWithWindows
    {
        get => StartupRegistration.IsEnabled;
        set => StartupRegistration.SetEnabled(value);
    }

    public void ShowNotification(string title, string body) => notifications.Show(title, body, onClick: showWindow);

    public string? Restart() => restart();

    public void OpenLogsFolder() => OpenFolder(AppFolders.Logs(SessionStore.Directory));

    public void OpenMessagesFolder() => OpenFolder(AppFolders.Messages(SessionStore.Directory));

    public void OpenCleansFolder() => OpenFolder(AppFolders.Cleans(SessionStore.Directory));

    private void OpenFolder(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            LogOpenFailed(logger, ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "A data folder could not be opened")]
    private static partial void LogOpenFailed(ILogger logger, Exception exception);
}
