namespace DyssCockpit.Presentation;

/// <summary>
/// What the settings page asks of the application around it: the things only the host can do,
/// kept behind an interface so the page's view model can be tested without Windows. The same
/// contract as HusqA Cockpit's, with a restart that reports why it failed.
/// </summary>
public interface IAppShell
{
    /// <summary>
    /// Whether the application starts with Windows, in the notification area. Setting it writes to
    /// the registry, which may be refused: it then throws, and the getter still says what is really there.
    /// </summary>
    bool StartsWithWindows { get; set; }

    /// <summary>Shows a Windows notification; a click on it brings the window back.</summary>
    void ShowNotification(string title, string body);

    /// <summary>
    /// Starts a new instance of the application and ends this one, to apply a new language. Only
    /// returns when Windows could not restart it, with the reason.
    /// </summary>
    string? Restart();

    /// <summary>Opens the folder holding the session, the settings and the logs.</summary>
    void OpenLogsFolder();
}
