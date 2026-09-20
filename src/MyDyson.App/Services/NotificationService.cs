using System.Drawing;
using System.Windows.Forms;

namespace MyDyson.App.Services;

/// <summary>
/// Windows Action Center notifications for robot events, via a tray icon's balloon tip. Simpler
/// than a packaged toast (no MSIX, no AppUserModelID) and enough for a desktop app: Windows renders
/// NotifyIcon balloons as ordinary toasts on 10/11.
/// </summary>
public sealed class NotificationService : IDisposable
{
    private readonly NotifyIcon _icon;
    private Action? _onClick;

    public NotificationService()
    {
        Icon appIcon;
        try { appIcon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? SystemIcons.Application; }
        catch { appIcon = SystemIcons.Application; }

        _icon = new NotifyIcon { Icon = appIcon, Text = "MyDyson", Visible = true };
        _icon.BalloonTipClicked += (_, _) => _onClick?.Invoke();
    }

    /// <summary>Shows a balloon; <paramref name="onClick"/> runs once if the user clicks it.</summary>
    public void Show(string title, string text, Action? onClick = null)
    {
        _onClick = onClick;
        _icon.BalloonTipTitle = title;
        _icon.BalloonTipText = text;
        _icon.ShowBalloonTip(8000);
    }

    public void Dispose() => _icon.Dispose();
}
