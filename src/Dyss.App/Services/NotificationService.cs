using Dyss.Presentation.Services;
using Microsoft.UI.Dispatching;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace Dyss.App.Services;

/// <summary>
/// Windows notifications for robot events, through the Windows App SDK: they land in the
/// notification centre, and a click on one brings the window back. If Windows refuses the
/// registration, robot events simply go without a notification.
/// </summary>
internal sealed class NotificationService : IDisposable
{
    private readonly bool _registered;
    private Action? _onClick;

    public NotificationService(DispatcherQueue ui)
    {
        try
        {
            var manager = AppNotificationManager.Default;
            // Raised on a background thread; subscribed before registering, as the SDK requires.
            manager.NotificationInvoked += (_, _) => ui.TryEnqueue(() => _onClick?.Invoke());
            manager.Register();
            _registered = true;
        }
        catch (Exception ex)
        {
            ErrorLog.Write("notifications", ex);
        }
    }

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
