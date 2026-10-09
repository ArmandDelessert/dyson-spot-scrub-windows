using System.Globalization;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using DyssCockpit.Core;

namespace DyssCockpit.Presentation.Services;

/// <summary>Which moments of a cleaning task the user is told about by a Windows notification.</summary>
public enum TaskNotificationMode
{
    /// <summary>No notification when a clean starts or ends.</summary>
    None,

    /// <summary>When the clean ends, or is given up by the robot. The default: it is what the application has always told.</summary>
    EndOnly,

    /// <summary>When the clean starts as well.</summary>
    StartAndEnd,
}

/// <summary>
/// The application's own settings, as opposed to the robot's: preferences that belong to the
/// installation, not to the account — what the map draws, the language, what the window does when
/// it is closed. Kept apart from the robot settings on purpose: everything on that page is sent to
/// the robot and visible from the phone app, whereas nothing here leaves this machine. Stored in
/// clear as settings.json next to the session, since none of it is sensitive.
/// </summary>
public sealed partial class AppSettings : ObservableObject
{
    private static string DefaultPath => Path.Combine(SessionStore.Directory, "settings.json");

    /// <summary>Where <see cref="Save"/> writes. Null on a plain instance, which then only lives for the run — that is what tests use.</summary>
    private string? _path;

    /// <summary>Furniture outlines on both maps. Off makes the rooms and the trail easier to read.</summary>
    [ObservableProperty] private bool _showFurniture = true;

    /// <summary>
    /// The stretches where the robot was only repositioning (the trail's "update" flag at 0).
    /// Off leaves just the parts where it actually worked, which is what tells you what got cleaned.
    /// </summary>
    [ObservableProperty] private bool _showTravelPath = true;

    /// <summary>Whether the map toolbar offers the PNG export at all.</summary>
    [ObservableProperty] private bool _showExportButton = true;

    /// <summary>
    /// Every message exchanged with the robot written to a daily file (see <see cref="MessageLog"/>).
    /// Not about drawing, but the same kind of preference: this machine's, never sent anywhere.
    /// Off by default.
    /// </summary>
    [ObservableProperty] private bool _recordMessages;

    /// <summary>Zones and furniture drawn or moved in the map manager land on the robot's 5 cm grid. On by default.</summary>
    [ObservableProperty] private bool _snapToGrid = true;

    /// <summary>
    /// The robot glides on the dashboard's map from one reported position to the next instead of
    /// jumping, at the cost of showing it a moment late. Off by default.
    /// </summary>
    [ObservableProperty] private bool _smoothRobotMotion;

    /// <summary>
    /// The surface cleaned: each stretch the robot worked on is drawn as wide as the robot, under the
    /// trail. Off by default, the trail alone being the lighter picture.
    /// </summary>
    [ObservableProperty] private bool _showCleanedArea;

    /// <summary>
    /// The language of the application: <c>"fr"</c>, <c>"en"</c>, or <c>"auto"</c> for Windows' own (French on a
    /// French Windows, English otherwise). Read once at start-up, see <see cref="ChosenLanguage"/>.
    /// </summary>
    [ObservableProperty] private string _language = "auto";

    /// <summary>
    /// Closing the window leaves the application in the notification area, still connected to the
    /// robot and still notifying; it quits from the icon's menu. On by default: the notifications
    /// are what the application is kept open for.
    /// </summary>
    [ObservableProperty] private bool _closeToTray = true;

    /// <summary>Whether the user has been told, once, that closing the window left the application running.</summary>
    [ObservableProperty] private bool _trayHintShown;

    /// <summary>
    /// Whether a Windows notification tells when a clean starts and when it ends, see
    /// <see cref="TaskNotificationMode"/>. Stored by name, so the file reads as what it means.
    /// </summary>
    [ObservableProperty] private TaskNotificationMode _taskNotifications = TaskNotificationMode.EndOnly;

    /// <summary>A Windows notification when the robot could not reach a room it was sent to. On by default.</summary>
    [ObservableProperty] private bool _notifyUnreachable = true;

    /// <summary>The numbers of days the settings page offers for keeping the logs and the message records; 0 is "for ever".</summary>
    public static readonly System.Collections.Immutable.ImmutableArray<int> RetentionChoices = [1, 3, 7, 14, 30, 90, 0];

    /// <summary>
    /// How many days back the log files are kept, 0 for ever. One of <see cref="RetentionChoices"/>:
    /// a number written into the file by hand that is not one of them is read as the default.
    /// </summary>
    [ObservableProperty] private int _logRetentionDays = 7;

    /// <summary>How many days back the records of the robot's messages are kept, 0 for ever. See <see cref="LogRetentionDays"/>.</summary>
    [ObservableProperty] private int _messageRetentionDays = 30;

    /// <summary>The language <see cref="Language"/> stands for on this machine.</summary>
    public AppLanguage ChosenLanguage => Language switch
    {
        "fr" => AppLanguage.French,
        "en" => AppLanguage.English,
        _ => Translation.FromCulture(CultureInfo.CurrentUICulture),
    };

    /// <summary>Raised after any of the above changes, once they have been written back to disk.</summary>
    public event Action? Changed;

    /// <summary>
    /// Reads the stored preferences, falling back to the defaults for anything missing or
    /// unreadable, and remembers changes from then on. <paramref name="path"/> is for tests.
    /// </summary>
    public static AppSettings Load(string? path = null)
    {
        var settings = new AppSettings { _path = path ?? DefaultPath };
        try
        {
            if (File.Exists(settings._path) && JsonSerializer.Deserialize<Stored>(File.ReadAllText(settings._path)) is { } s)
            {
                settings._loading = true;
                settings.ShowFurniture = s.ShowFurniture ?? true;
                settings.ShowTravelPath = s.ShowTravelPath ?? true;
                settings.ShowExportButton = s.ShowExportButton ?? true;
                settings.RecordMessages = s.RecordMessages ?? false;
                settings.SnapToGrid = s.SnapToGrid ?? true;
                settings.SmoothRobotMotion = s.SmoothRobotMotion ?? false;
                settings.ShowCleanedArea = s.ShowCleanedArea ?? false;
                settings.Language = s.Language is "fr" or "en" ? s.Language : "auto";
                settings.CloseToTray = s.CloseToTray ?? true;
                settings.TrayHintShown = s.TrayHintShown ?? false;
                settings.TaskNotifications = Enum.TryParse<TaskNotificationMode>(s.TaskNotifications, out var mode) && Enum.IsDefined(mode) ? mode : TaskNotificationMode.EndOnly;
                settings.NotifyUnreachable = s.NotifyUnreachable ?? true;
                settings.LogRetentionDays = s.LogRetentionDays is { } logDays && RetentionChoices.Contains(logDays) ? logDays : 7;
                settings.MessageRetentionDays = s.MessageRetentionDays is { } messageDays && RetentionChoices.Contains(messageDays) ? messageDays : 30;
                settings._loading = false;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // A corrupt or unreadable preferences file is not worth failing a launch over.
        }
        return settings;
    }

    /// <summary>
    /// The stored preferences for a run that must leave the user's files alone, a screenshot taken
    /// while the application may be running beside it: whatever changes applies to this run only,
    /// and messages are not recorded, since both processes would write the same daily file and the
    /// one that fails would turn recording off for good.
    /// </summary>
    public static AppSettings LoadReadOnly(string? path = null)
    {
        var settings = Load(path);
        settings._path = null;
        settings._loading = true;
        settings.RecordMessages = false;
        settings._loading = false;
        return settings;
    }

    private bool _loading;

    private void Save()
    {
        if (_loading) return;
        Changed?.Invoke();
        if (_path is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(new Stored(ShowFurniture, ShowTravelPath, ShowExportButton, RecordMessages, SnapToGrid, SmoothRobotMotion, Language, CloseToTray, TrayHintShown, ShowCleanedArea, TaskNotifications.ToString(), NotifyUnreachable, LogRetentionDays, MessageRetentionDays)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Same: the setting still applies for this run, it just will not be remembered.
        }
    }

    partial void OnShowFurnitureChanged(bool value) => Save();
    partial void OnShowTravelPathChanged(bool value) => Save();
    partial void OnShowExportButtonChanged(bool value) => Save();
    partial void OnRecordMessagesChanged(bool value) => Save();
    partial void OnSnapToGridChanged(bool value) => Save();
    partial void OnSmoothRobotMotionChanged(bool value) => Save();
    partial void OnShowCleanedAreaChanged(bool value) => Save();
    partial void OnLanguageChanged(string value) => Save();
    partial void OnCloseToTrayChanged(bool value) => Save();
    partial void OnTrayHintShownChanged(bool value) => Save();
    partial void OnTaskNotificationsChanged(TaskNotificationMode value) => Save();
    partial void OnNotifyUnreachableChanged(bool value) => Save();
    partial void OnLogRetentionDaysChanged(int value) => Save();
    partial void OnMessageRetentionDaysChanged(int value) => Save();

    /// <summary>Nullable members so a file written by an older version keeps the defaults for what it lacks.</summary>
    private sealed record Stored(bool? ShowFurniture, bool? ShowTravelPath, bool? ShowExportButton, bool? RecordMessages = null, bool? SnapToGrid = null, bool? SmoothRobotMotion = null, string? Language = null, bool? CloseToTray = null, bool? TrayHintShown = null, bool? ShowCleanedArea = null, string? TaskNotifications = null, bool? NotifyUnreachable = null, int? LogRetentionDays = null, int? MessageRetentionDays = null);
}
