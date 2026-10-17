using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using DyssCockpit.Core;

namespace DyssCockpit.Presentation.Services;

/// <summary>Where the window was, in screen pixels, and whether it was maximized; the position and size then are those of the window before it was maximized.</summary>
public sealed record WindowBounds(int X, int Y, int Width, int Height, bool Maximized);

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
/// <para>
/// The class is its own file format: each observable property below is a setting, written under
/// its own name, and declared once. A key the file lacks keeps the initial value, so a file
/// written by an older version gets the defaults for what came after it; a value that is not
/// one this version offers is put back to its default by <see cref="Validate"/>.
/// </para>
/// </summary>
public sealed partial class AppSettings : ObservableObject
{
    private static string DefaultPath => Path.Combine(SessionStore.Directory, "settings.json");

    /// <summary>The file is meant to be read, and edited, by a person: one setting a line, as HusqA Cockpit's is.</summary>
    private static readonly JsonSerializerOptions FileFormat = new() { WriteIndented = true };

    /// <summary>Where <see cref="Save"/> writes. Null on a plain instance, which then only lives for the run — that is what tests use.</summary>
    private string? _path;

    /// <summary>Furniture outlines on both maps. Off makes the rooms and the trail easier to read.</summary>
    [ObservableProperty] private bool _showFurniture = true;

    /// <summary>
    /// The stretches where the robot was only repositioning (the trail's "update" flag at 0).
    /// Off leaves just the parts where it actually worked, which is what tells you what got cleaned.
    /// </summary>
    [ObservableProperty] private bool _showTravelPath = true;

    /// <summary>Whether the map toolbar offers the PNG export at all. Off by default: the export is an occasional need, not a daily one.</summary>
    [ObservableProperty] private bool _showExportButton;

    /// <summary>
    /// Every message exchanged with the robot written to a daily file (see <see cref="MessageLog"/>).
    /// Not about drawing, but the same kind of preference: this machine's, never sent anywhere.
    /// Off by default.
    /// </summary>
    [ObservableProperty] private bool _recordMessages;

    /// <summary>
    /// The map coordinates of the pointer, in metres, shown just above it on every map: what the robot's own
    /// messages and the protocol notes speak in, to read a spot off the map. Off by default.
    /// </summary>
    [ObservableProperty] private bool _showPointerCoordinates;

    /// <summary>
    /// A rectangle being drawn on a map, or the zone to clean, shows its corners' coordinates and the
    /// length of its sides, in metres. Off by default.
    /// </summary>
    [ObservableProperty] private bool _showShapeDimensions;

    /// <summary>Zones and furniture drawn or moved in the map manager land on the robot's 5 cm grid. On by default.</summary>
    [ObservableProperty] private bool _snapToGrid = true;

    /// <summary>
    /// The robot glides on the dashboard's map from one reported position to the next instead of
    /// jumping, at the cost of showing it a moment late. Off by default.
    /// </summary>
    [ObservableProperty] private bool _smoothRobotMotion;

    /// <summary>
    /// The surface cleaned: each stretch the robot worked on is drawn as wide as the robot, under the
    /// trail. On by default: it is what shows what got cleaned; off leaves the trail alone, the lighter picture.
    /// </summary>
    [ObservableProperty] private bool _showCleanedArea = true;

    /// <summary>
    /// The language of the application, as the name of a culture, which is what HusqA Cockpit writes too:
    /// <c>"fr-FR"</c>, <c>"en-US"</c>, or empty for Windows' own (French on a French Windows, English
    /// otherwise). Read once at start-up, see <see cref="ChosenLanguage"/>. A file written by an earlier
    /// version (<c>"fr"</c>, <c>"en"</c>, <c>"auto"</c>) is read all the same, see <see cref="NormalizeLanguage"/>.
    /// </summary>
    [ObservableProperty] private string _language = "";

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
    [ObservableProperty]
    [property: JsonConverter(typeof(TaskNotificationsByName))]
    private TaskNotificationMode _taskNotifications = TaskNotificationMode.EndOnly;

    /// <summary>A Windows notification when the robot starts to report a fault that needs someone, see <see cref="RobotFaultWatcher"/>. On by default.</summary>
    [ObservableProperty] private bool _notifyRobotFault = true;

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

    /// <summary>The numbers of days the settings page offers for keeping the cleans of the history; 0 is "for ever".</summary>
    public static readonly System.Collections.Immutable.ImmutableArray<int> CleanArchiveChoices = [30, 90, 180, 365, 730, 0];

    /// <summary>
    /// Every clean the history shows is also kept on this computer, see <see cref="CleanArchive"/>:
    /// the Dyson cloud only holds the last few. Off by default, since it fills a folder.
    /// </summary>
    [ObservableProperty] private bool _archiveCleans;

    /// <summary>How many days back the kept cleans are kept, 0 for ever; one of <see cref="CleanArchiveChoices"/>, a year by default.</summary>
    [ObservableProperty] private int _cleanArchiveRetentionDays = 365;

    /// <summary>The language <see cref="Language"/> stands for on this machine.</summary>
    [JsonIgnore]
    public AppLanguage ChosenLanguage => AppLanguages.FromTag(NormalizeLanguage(Language)) ?? Translation.FromCulture(CultureInfo.CurrentUICulture);

    /// <summary>
    /// What is stored for a language: its tag (<c>"fr-FR"</c> for any French, <c>"fr"</c> and <c>"fr-CH"</c> included, <c>"de-DE"</c>
    /// for any German…), and empty for anything the application does not speak, <c>"auto"</c> of the first versions included,
    /// which means Windows' own.
    /// </summary>
    public static string NormalizeLanguage(string? language) =>
        AppLanguages.FromTag(language) is { } known ? AppLanguages.Of(known).Tag : "";

    /// <summary>Raised after any of the above changes, once they have been written back to disk.</summary>
    public event Action? Changed;

    /// <summary>
    /// Reads the stored preferences, falling back to the defaults for anything missing or
    /// unreadable, and remembers changes from then on. <paramref name="path"/> is for tests.
    /// </summary>
    public static AppSettings Load(string? path = null)
    {
        path ??= DefaultPath;
        AppSettings? settings = null;
        try
        {
            // Read into an instance that writes nowhere and that nobody listens to yet: setting its
            // properties from the file is not a change to save.
            if (File.Exists(path)) settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), FileFormat);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            // A corrupt or unreadable preferences file is not worth failing a launch over.
        }
        settings ??= new AppSettings();
        settings._loading = true;
        settings.Validate();
        settings._loading = false;
        settings._path = path;
        return settings;
    }

    /// <summary>
    /// Puts back to its default whatever the file holds that this version does not offer: a
    /// retention written in by hand that the list lacks, an empty window, a language of an
    /// earlier version (see <see cref="NormalizeLanguage"/>).
    /// </summary>
    private void Validate()
    {
        Language = NormalizeLanguage(Language);
        if (!RetentionChoices.Contains(LogRetentionDays)) LogRetentionDays = 7;
        if (!RetentionChoices.Contains(MessageRetentionDays)) MessageRetentionDays = 30;
        if (!CleanArchiveChoices.Contains(CleanArchiveRetentionDays)) CleanArchiveRetentionDays = 365;
        if (Window is not { Width: > 0, Height: > 0 }) Window = null;
    }

    /// <summary>
    /// The stored preferences for a run that must leave the user's files alone, a screenshot taken
    /// while the application may be running beside it: whatever changes applies to this run only,
    /// the kept cleans are only read (see <see cref="LeavesFilesAlone"/>), and messages are not
    /// recorded, since both processes would write the same daily file and the one that fails would
    /// turn recording off for good.
    /// </summary>
    public static AppSettings LoadReadOnly(string? path = null)
    {
        var settings = Load(path);
        settings._path = null;
        settings.LeavesFilesAlone = true;
        settings._loading = true;
        settings.RecordMessages = false;
        settings._loading = false;
        return settings;
    }

    /// <summary>
    /// True for a run that must leave the user's files alone (see <see cref="LoadReadOnly"/>): besides the
    /// settings, the kept cleans are read but neither added to nor cleared.
    /// </summary>
    [JsonIgnore]
    public bool LeavesFilesAlone { get; internal set; }

    private bool _loading;

    /// <summary>
    /// Every observable property is a setting: any change is written back and told through
    /// <see cref="Changed"/>, before the change itself is raised, as each setting used to do on its own.
    /// </summary>
    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        if (!_loading)
        {
            Changed?.Invoke();
            Write();
        }
        base.OnPropertyChanged(e);
    }

    /// <summary>Where the window was last, and its size, for the next start; null until it has been moved or resized.</summary>
    [JsonInclude]
    public WindowBounds? Window { get; private set; }

    /// <summary>
    /// Remembers where the window is. Written like any setting, but without <see cref="Changed"/>: the maps
    /// redraw on that, and a window being dragged is no reason to.
    /// </summary>
    public void RememberWindow(WindowBounds bounds)
    {
        if (Window == bounds) return;
        Window = bounds;
        if (!_loading) Write();
    }

    private void Write()
    {
        if (_path is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(this, FileFormat));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Same: the setting still applies for this run, it just will not be remembered.
        }
    }

    /// <summary>
    /// <see cref="TaskNotifications"/> by its name, as the file has always held it. A name this
    /// version does not know, or anything but a name, reads as the default rather than failing the
    /// whole file.
    /// </summary>
    private sealed class TaskNotificationsByName : JsonConverter<TaskNotificationMode>
    {
        public override TaskNotificationMode Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.String
                && Enum.TryParse<TaskNotificationMode>(reader.GetString(), out var mode) && Enum.IsDefined(mode))
                return mode;
            reader.Skip();
            return TaskNotificationMode.EndOnly;
        }

        public override void Write(Utf8JsonWriter writer, TaskNotificationMode value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToString());
    }
}
