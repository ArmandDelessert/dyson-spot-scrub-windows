using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace DyssCockpit.App.Services;

/// <summary>
/// Windows notifications for robot events, through the Windows App SDK: they land in the
/// notification centre, and a click on one brings the window back. If Windows refuses the
/// registration, robot events simply go without a notification.
/// </summary>
internal sealed partial class NotificationService : IDisposable
{
    /// <summary>What Windows shows above each notification, and names this application's entry in its notification settings.</summary>
    private const string AppName = "DySS Cockpit";

    /// <summary>The application's icon, shown beside each notification.</summary>
    private static string IconPath => Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");

    private readonly bool _registered;
    private Action? _onClick;

    public NotificationService(DispatcherQueue ui, ILogger<NotificationService> logger)
    {
        try
        {
            var manager = AppNotificationManager.Default;
            // Raised on a background thread; subscribed before registering, as the SDK requires.
            manager.NotificationInvoked += (_, _) => ui.TryEnqueue(() => _onClick?.Invoke());
            // Registered with a name and an icon of its own: without them Windows names the
            // notifications after the executable (DyssCockpit) and draws its icon from the file.
            // It takes both from here at the first notification sent from a folder and keeps
            // them (an .ico is accepted): changing them later shows up from another folder, or
            // once the folder's entries under HKCU\Software\Classes\AppUserModelId are removed.
            manager.Register(AppName, new Uri(IconPath));
            _registered = true;
        }
        catch (Exception ex)
        {
            LogRegistrationFailed(logger, ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Windows refused the notification registration; robot events will go without one")]
    private static partial void LogRegistrationFailed(ILogger logger, Exception exception);

    /// <summary>Shows a notification; <paramref name="onClick"/> runs if the user clicks it.</summary>
    public void Show(string title, string text, Action? onClick = null)
    {
        if (!_registered) return;
        _onClick = onClick;
        AppNotificationManager.Default.Show(new AppNotificationBuilder().AddText(title).AddText(text).BuildNotification());
    }

    /// <summary>A notification clicked once the application has closed has nothing left to bring back.</summary>
    public void Dispose()
    {
        if (_registered) AppNotificationManager.Default.Unregister();
    }
}
