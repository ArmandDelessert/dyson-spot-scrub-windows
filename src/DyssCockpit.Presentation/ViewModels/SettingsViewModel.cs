using System.Reflection;
using System.Windows.Input;
using System.Runtime.CompilerServices;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DyssCockpit.Core;
using DyssCockpit.Presentation.Services;

using static DyssCockpit.Core.Translation;

namespace DyssCockpit.Presentation.ViewModels;

/// <summary>
/// The application's Paramètres page: what DySS Cockpit does on this computer, as opposed to the
/// robot's settings (<see cref="RobotSettingsViewModel"/>), which are all sent to the robot. Nothing
/// here leaves the machine. It lays out <see cref="AppSettings"/> in the sections HusqA Cockpit's
/// page has — notifications, behavior, language, then the map's, then about — with the same names,
/// and each change is written to the settings file at once.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    /// <summary>The values of <see cref="AppSettings.Language"/>, in the order the language list shows them.</summary>
    private static readonly string[] Languages = ["auto", "fr", "en"];

    private readonly AppSettings _settings;
    private readonly IAppShell _shell;
    private readonly IDialogService _dialogs;
    /// <summary>The language the application is running in: a different choice is only applied at the next start.</summary>
    private readonly AppLanguage _running;
    private readonly bool _initialized;

    /// <param name="running">The language the application is running in; null for the one it was started in.</param>
    /// <param name="logout">What the "Se déconnecter" button does: the dashboard's own, which asks first. Nothing when null.</param>
    /// <param name="accountEmail">The account the application is signed in to, shown above that button.</param>
    public SettingsViewModel(AppSettings settings, IAppShell shell, IDialogService dialogs, AppLanguage? running = null,
        ICommand? logout = null, string accountEmail = "")
    {
        LogoutCommand = logout ?? new RelayCommand(() => { });
        AccountText = string.IsNullOrEmpty(accountEmail) ? "" : T($"Connecté avec {accountEmail}", $"Signed in as {accountEmail}");
        _running = running ?? Translation.Current;
        _settings = settings;
        _shell = shell;
        _dialogs = dialogs;
        VersionText = VersionTextOf(Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);
        LanguageIndex = Math.Max(0, Array.IndexOf(Languages, settings.Language));
        TaskNotificationsIndex = Math.Max(0, Array.IndexOf(TaskNotificationModes, settings.TaskNotifications));
        LogRetentionIndex = Math.Max(0, AppSettings.RetentionChoices.IndexOf(settings.LogRetentionDays));
        MessageRetentionIndex = Math.Max(0, AppSettings.RetentionChoices.IndexOf(settings.MessageRetentionDays));
        StartWithWindows = shell.StartsWithWindows;
        // A language chosen earlier and not yet applied: the page may have been opened again since.
        IsRestartRequired = settings.ChosenLanguage != _running;
        _initialized = true;
    }

    /// <summary>The version the build carries, without the suffix naming the commit it was built from.</summary>
    public static string VersionOf(Assembly assembly) =>
        VersionOf(assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);

    public static string VersionOf(string? informationalVersion) =>
        string.IsNullOrWhiteSpace(informationalVersion) ? "?" : informationalVersion.Split('+')[0];

    /// <summary>The first characters of the commit the build was made from, which the build puts after a "+"; null when it says none.</summary>
    public static string? CommitOf(string? informationalVersion)
    {
        var parts = informationalVersion?.Split('+', 2);
        return parts is { Length: 2 } && parts[1].Trim() is { Length: > 0 } commit ? commit[..Math.Min(7, commit.Length)] : null;
    }

    /// <summary>
    /// "DySS Cockpit 0.1.0 (a1b2c3d)": the version, then the commit it was built from. The commit is
    /// always given rather than only for builds numbered 0.x: it is what tells apart two builds of
    /// the same development version, and costs a release nothing.
    /// </summary>
    public static string VersionTextOf(string? informationalVersion) =>
        $"DySS Cockpit {VersionOf(informationalVersion)}" + (CommitOf(informationalVersion) is { } commit ? $" ({commit})" : "");

    // ----- Notifications -----

    /// <summary>The values of <see cref="AppSettings.TaskNotifications"/>, in the order the list shows them.</summary>
    private static readonly TaskNotificationMode[] TaskNotificationModes = [TaskNotificationMode.None, TaskNotificationMode.EndOnly, TaskNotificationMode.StartAndEnd];

    /// <summary>0 for none, 1 for the end of a clean only, 2 for its start and end.</summary>
    [ObservableProperty] private int _taskNotificationsIndex;

    partial void OnTaskNotificationsIndexChanged(int value)
    {
        if (!_initialized || value < 0 || value >= TaskNotificationModes.Length) return;
        _settings.TaskNotifications = TaskNotificationModes[value];
    }

    public bool NotifyRobotFault
    {
        get => _settings.NotifyRobotFault;
        set => Update(_settings.NotifyRobotFault, value, v => _settings.NotifyRobotFault = v);
    }

    public bool NotifyUnreachable
    {
        get => _settings.NotifyUnreachable;
        set => Update(_settings.NotifyUnreachable, value, v => _settings.NotifyUnreachable = v);
    }

    [RelayCommand]
    private void SendTestNotification() =>
        _shell.ShowNotification(T("Notification de test", "Test notification"),
            T("Voici comment DySS Cockpit vous préviendra pour votre robot.", "This is how DySS Cockpit will notify you about your robot."));

    // ----- Behavior -----

    public bool CloseToTray
    {
        get => _settings.CloseToTray;
        set => Update(_settings.CloseToTray, value, v => _settings.CloseToTray = v);
    }

    [ObservableProperty] private bool _startWithWindows;

    /// <summary>Writes the registry entry; when Windows refuses, the switch goes back to what is really there, and the user is told.</summary>
    partial void OnStartWithWindowsChanged(bool value)
    {
        if (!_initialized || value == _shell.StartsWithWindows) return;
        try
        {
            _shell.StartsWithWindows = value;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            StartWithWindows = _shell.StartsWithWindows;
            _ = _dialogs.AlertAsync(T("Démarrer avec Windows", "Start with Windows"), ex.Message);
        }
    }

    // ----- Language -----

    /// <summary>0 follows Windows, 1 is French, 2 is English.</summary>
    [ObservableProperty] private int _languageIndex;

    /// <summary>The language chosen is not the one running: every text is laid out once, as the window is built.</summary>
    [ObservableProperty] private bool _isRestartRequired;

    partial void OnLanguageIndexChanged(int value)
    {
        if (!_initialized || value < 0 || value >= Languages.Length) return;
        _settings.Language = Languages[value];
        IsRestartRequired = _settings.ChosenLanguage != _running;
    }

    [RelayCommand]
    private async Task RestartAsync()
    {
        // Only comes back when Windows could not restart the application.
        if (_shell.Restart() is { } failure)
            await _dialogs.AlertAsync(T("Changer de langue", "Change language"),
                T($"Le redémarrage a échoué ({failure}). Relancez l'application pour changer de langue.",
                  $"The restart failed ({failure}). Start the application again to change its language."));
    }

    // ----- Map -----

    public bool ShowFurniture
    {
        get => _settings.ShowFurniture;
        set => Update(_settings.ShowFurniture, value, v => _settings.ShowFurniture = v);
    }

    public bool ShowTravelPath
    {
        get => _settings.ShowTravelPath;
        set => Update(_settings.ShowTravelPath, value, v => _settings.ShowTravelPath = v);
    }

    public bool ShowCleanedArea
    {
        get => _settings.ShowCleanedArea;
        set => Update(_settings.ShowCleanedArea, value, v => _settings.ShowCleanedArea = v);
    }

    public bool ShowExportButton
    {
        get => _settings.ShowExportButton;
        set => Update(_settings.ShowExportButton, value, v => _settings.ShowExportButton = v);
    }

    public bool SmoothRobotMotion
    {
        get => _settings.SmoothRobotMotion;
        set => Update(_settings.SmoothRobotMotion, value, v => _settings.SmoothRobotMotion = v);
    }

    // ----- Retention -----

    /// <summary>The position of <see cref="AppSettings.LogRetentionDays"/> among <see cref="AppSettings.RetentionChoices"/>: 1, 3, 7, 14, 30, 90 days, then for ever.</summary>
    [ObservableProperty] private int _logRetentionIndex;

    /// <summary>The same for <see cref="AppSettings.MessageRetentionDays"/>.</summary>
    [ObservableProperty] private int _messageRetentionIndex;

    partial void OnLogRetentionIndexChanged(int value)
    {
        if (!_initialized || value < 0 || value >= AppSettings.RetentionChoices.Length) return;
        _settings.LogRetentionDays = AppSettings.RetentionChoices[value];
    }

    partial void OnMessageRetentionIndexChanged(int value)
    {
        if (!_initialized || value < 0 || value >= AppSettings.RetentionChoices.Length) return;
        _settings.MessageRetentionDays = AppSettings.RetentionChoices[value];
    }

    // ----- About -----

    public string VersionText { get; }

    // ----- Account -----

    /// <summary>"Connecté avec …" and the e-mail of the account; empty when it is not known.</summary>
    public string AccountText { get; }

    /// <summary>Signs out of the MyDyson account, after asking: the dashboard's own command, which then leaves for the login.</summary>
    public ICommand LogoutCommand { get; }

    [RelayCommand]
    private void OpenLogsFolder() => _shell.OpenLogsFolder();

    /// <summary>Writes a changed value to the settings, which save themselves, and tells the page.</summary>
    private void Update<T>(T current, T value, Action<T> apply, [CallerMemberName] string? property = null)
    {
        if (EqualityComparer<T>.Default.Equals(current, value)) return;
        apply(value);
        OnPropertyChanged(property);
    }
}
