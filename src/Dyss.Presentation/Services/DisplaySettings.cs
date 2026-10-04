using System.Globalization;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using Dyss.Core;

namespace Dyss.Presentation.Services;

/// <summary>
/// What this window draws, as opposed to what the robot does: preferences that belong to the
/// installation, not to the account. Kept apart from the Réglages tab on purpose — everything
/// there is sent to the robot and visible from the phone app, whereas nothing here leaves this
/// machine. Stored in clear next to the session, since none of it is sensitive.
/// </summary>
public sealed partial class DisplaySettings : ObservableObject
{
    private static string DefaultPath => Path.Combine(SessionStore.Directory, "display.json");

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
    /// The language of the application: <c>"fr"</c>, <c>"en"</c>, or <c>"auto"</c> for Windows' own (French on a
    /// French Windows, English otherwise). Read once at start-up, see <see cref="ChosenLanguage"/>.
    /// </summary>
    [ObservableProperty] private string _language = "auto";

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
    public static DisplaySettings Load(string? path = null)
    {
        var settings = new DisplaySettings { _path = path ?? DefaultPath };
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
                settings.Language = s.Language is "fr" or "en" ? s.Language : "auto";
                settings._loading = false;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // A corrupt or unreadable preferences file is not worth failing a launch over.
        }
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
            File.WriteAllText(_path, JsonSerializer.Serialize(new Stored(ShowFurniture, ShowTravelPath, ShowExportButton, RecordMessages, SnapToGrid, SmoothRobotMotion, Language)));
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
    partial void OnLanguageChanged(string value) => Save();

    /// <summary>Nullable members so a file written by an older version keeps the defaults for what it lacks.</summary>
    private sealed record Stored(bool? ShowFurniture, bool? ShowTravelPath, bool? ShowExportButton, bool? RecordMessages = null, bool? SnapToGrid = null, bool? SmoothRobotMotion = null, string? Language = null);
}
