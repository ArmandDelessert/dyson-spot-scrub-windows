using DyssCockpit.App.Rendering;
using DyssCockpit.App.Services;
using DyssCockpit.App.Views;
using DyssCockpit.Core;
using DyssCockpit.Presentation.Map;
using DyssCockpit.Presentation.Services;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.AppLifecycle;

using static DyssCockpit.Core.Translation;

namespace DyssCockpit.App;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "The application's lifetime is its process's: the notifications and the icon are disposed when the window closes, the log when the process exits.")]
public partial class App : Application
{
    /// <summary>The log on disk, %LocalAppData%\DySS Cockpit\Logs\dyss-cockpit-2026-10-05.log: one file a day, kept as long as the settings say.</summary>
    private readonly FileLoggerProvider _logFile;
    private readonly ILoggerFactory _loggers;
    /// <summary>The preferences of this run, shared by the window (where it was) and the dashboard (everything else): two copies would write over each other.</summary>
    private readonly AppSettings _settings;
    private readonly ILogger _logger;
    private MainWindow? _window;
    private NotificationService? _notifications;
    /// <summary>The dashboard, while it is on show.</summary>
    private ShellView? _shell;
    /// <summary>The icon in the notification area; null for a screenshot, or when Windows would not have it.</summary>
    private TrayIcon? _tray;
    private bool _trayShowsAlert;
    /// <summary>Set once the user chose to quit: closing the window then closes it for real.</summary>
    private bool _exiting;
    /// <summary>What this run was started with: mostly nothing, or a diagnostic one-shot (see App.Diagnostics.cs).</summary>
    private readonly LaunchOptions _options = LaunchOptions.Parse(Environment.GetCommandLineArgs().Skip(1).ToArray());

    public App()
    {
        // The language comes first: the XAML resources loaded just below already carry texts.
        var stored = _settings = AppSettings.Load();
        Translation.Current = _options.Language switch
        {
            "fr" => AppLanguage.French,
            "en" => AppLanguage.English,
            _ => stored.ChosenLanguage,
        };
        // What is too old is cleared whether or not anything is being written to it now.
        using (var records = new MessageLog(keepDays: stored.MessageRetentionDays)) records.Purge();
        new CleanArchive(AppFolders.Cleans(SessionStore.Directory)).Purge(stored.CleanArchiveRetentionDays);
        _logFile = new FileLoggerProvider(AppFolders.Logs(SessionStore.Directory), retentionDays: stored.LogRetentionDays);
        _loggers = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Information).AddProvider(_logFile));
        _logger = _loggers.CreateLogger<App>();
        // Written out on the way out, whichever it is: the window closed, a headless export done.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => _logFile.Dispose();
        InitializeComponent();
        // Last resort for anything a command or event handler lets escape: tell the user and keep
        // the window open rather than vanishing without a word. Real bugs still surface — as a
        // dialog instead of a crash, and in the log — so this hides nothing, it just doesn't
        // lose the session.
        UnhandledException += (_, e) =>
        {
            Console.Error.WriteLine(e.Exception);
            LogUnhandled(_logger, e.Exception);
            e.Handled = true;
            _ = ShowErrorAsync(T("Erreur inattendue", "Unexpected error"), e.Exception.Message);
        };
        // Off the UI thread nothing can be saved any more — the process is going down — but the
        // log at least says why, written out at once: no exit event follows a crash.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            LogCrash(_logger, e.ExceptionObject as Exception);
            _logFile.Dispose();
        };
        // A forgotten task that failed: harmless to the process, but worth a trace.
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            LogUnobservedTask(_logger, e.Exception);
            e.SetObserved();
        };
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Unhandled exception in the interface")]
    private static partial void LogUnhandled(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Critical, Message = "Unhandled exception, the application stops")]
    private static partial void LogCrash(ILogger logger, Exception? exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A task failed with nobody waiting for it")]
    private static partial void LogUnobservedTask(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "The application could not start")]
    private static partial void LogStartFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The account's devices could not be loaded")]
    private static partial void LogDevicesFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "No icon in the notification area: closing the window will quit")]
    private static partial void LogTrayUnavailable(ILogger logger, Exception exception);

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            await RunAsync(_options);
        }
        catch (Exception ex)
        {
            // Anything escaping start-up would otherwise leave a window with nothing in it, which
            // is what an unreachable network used to do.
            Console.Error.WriteLine(ex);
            LogStartFailed(_logger, ex);
            if (await ShowErrorAsync("DySS Cockpit", T($"L'application n'a pas pu démarrer : {ex.Message}", $"The application could not start: {ex.Message}"))) _window?.Close();
            else Exit();
        }
    }

    private async Task RunAsync(LaunchOptions options)
    {
        // A map or the icon to a file: done without a window.
        if (await ExportAsync(options)) return;

        var theme = ThemeOf(options);
        var screenshot = options.IsScreenshot;
        // A screenshot is always the same size, wherever the window was left.
        _window = new MainWindow(screenshot ? null : _settings);
        if (theme != ElementTheme.Default) _window.ForceTheme(theme);
        // Started with Windows, with a session to resume: straight to the notification area, the
        // window left closed. A login to fill in opens it all the same.
        if (screenshot || !options.Minimized || !SessionStore.Exists) _window.Activate();
        _notifications = new NotificationService(_window.DispatcherQueue, _loggers.CreateLogger<NotificationService>());
        var shell = new AppShell(_notifications, () => Restart().ToString(), ShowWindow, _loggers.CreateLogger<AppShell>());
        _window.Closed += (_, _) =>
        {
            _notifications.Dispose();
            _tray?.Dispose();
        };
        if (screenshot) _ = ScreenshotAsync(options);
        else
        {
            // A one-shot screenshot has no business in the notification area.
            CreateTrayIcon();
            _window.AppWindow.Closing += OnWindowClosing;
            // A second launch, redirected here by Program.Main; raised off the UI thread.
            AppInstance.GetCurrent().Activated += (_, e) => _window.DispatcherQueue.TryEnqueue(() => OnActivated(e));
        }

        // --login starts at the login whatever session is stored, mainly for screenshots.
        var skipStored = options.Login;

        // Loops so that logging out from the dashboard comes back here to sign in again.
        while (true)
        {
            var ctx = await OpenAccountAsync(skipStored ? null : RobotContext.FromStoredSession(_loggers));
            skipStored = false;
            if (ctx is null)
            {
                _window.Close();
                return;
            }

            // A screenshot may run beside the application: it reads the preferences, never writes them.
            var settings = screenshot ? AppSettings.LoadReadOnly() : _settings;
            // ... and what it was told to speak is what it shows as chosen, so no restart is asked for.
            if (screenshot && options.Language is { } language) settings.Language = language == "fr" ? "fr-FR" : "en-US";
            _shell = new ShellView(_window, ctx, _notifications, shell, settings);
            _window.Show(_shell);
            _shell.Start();
            var shown = _shell.ViewModel.AppSettings;
            shown.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(AppSettings.LogRetentionDays)) _logFile.RetentionDays = shown.LogRetentionDays;
            };
            _shell.ViewModel.SummaryChanged += UpdateTray;
            UpdateTray();
            var loggedOut = await _shell.Finished;
            _shell = null;
            UpdateTray();
            if (!loggedOut) return;   // the window closed
            // else: back to the login.
        }
    }

    /// <summary>
    /// Gets as far as a robot to show: logs in when there is no session or the stored one is
    /// refused, and waits for the network when Dyson cannot be reached. Null when the user gives
    /// up (quits the wait) or the account has no device; never returns if the window is closed.
    /// </summary>
    private async Task<RobotContext?> OpenAccountAsync(RobotContext? ctx)
    {
        while (true)
        {
            if (ctx is null)
            {
                var login = new LoginView(_loggers);
                _window!.Show(login);
                if (!_window.AppWindow.IsVisible) _window.BringToFront();
                ctx = await login.Result;
            }

            try
            {
                if (await ctx.LoadDevicesAsync() is not null) return ctx;
                await ShowErrorAsync("DySS Cockpit", T("Aucun appareil sur ce compte.", "No device on this account."));
                await ctx.DisposeAsync();
                return null;
            }
            catch (DysonAuthException)
            {
                // Token expired: log in again.
                SessionStore.Delete();
                await ctx.DisposeAsync();
                ctx = null;
            }
            catch (Exception ex)
            {
                // No network yet, DNS failing, Dyson down: the same session will do once it is back.
                LogDevicesFailed(_logger, ex);
                var wait = new ConnectionWaitView(ex.Message);
                _window!.Show(wait);
                if (!await wait.Decision)
                {
                    await ctx.DisposeAsync();
                    return null;
                }
            }
        }
    }

    /// <summary>A message the user has to acknowledge. False when there is no window yet to show it on.</summary>
    private async Task<bool> ShowErrorAsync(string title, string message)
    {
        if (_window is null) return false;
        await _window.EnsureShownAsync();
        if (_window.Content?.XamlRoot is not { } root) return false;
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            CloseButtonText = "OK",
            XamlRoot = root,
            RequestedTheme = _window.Theme,
            Style = (Style)Resources["DefaultContentDialogStyle"],
        };
        try { await dialog.ShowAsync(); }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            // Another dialog is already open: the log has it.
        }
        return true;
    }

    // ---- In the background ------------------------------------------------------------------

    private void ShowWindow() => _window?.BringToFront();

    /// <summary>A second launch, or a notification clicked while the application ran: the window comes back, on the history for a notification.</summary>
    private void OnActivated(AppActivationArguments args)
    {
        ShowWindow();
        if (args.Kind == ExtendedActivationKind.AppNotification) _shell?.SelectTab(1);
    }

    /// <summary>
    /// Closing the dashboard's window leaves the application in the notification area, when so
    /// chosen and when the icon is there: the robot is still followed and its notifications still
    /// come. The first time, a notification says so.
    /// </summary>
    private void OnWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_exiting || _tray is null || _shell?.ViewModel.AppSettings is not { CloseToTray: true } settings) return;
        args.Cancel = true;
        sender.Hide();
        if (settings.TrayHintShown) return;
        settings.TrayHintShown = true;
        _notifications?.Show("DySS Cockpit",
            T("L'application reste ouverte dans la zone de notification pour suivre le robot. Pour la quitter, utilisez le menu de son icône.",
              "The application stays open in the notification area to keep track of the robot. To quit it, use its icon's menu."),
            onClick: ShowWindow);
    }

    private static string IconPath(bool alert) => Path.Combine(AppContext.BaseDirectory, "Assets", alert ? "AppIconAlert.ico" : "AppIcon.ico");

    private void CreateTrayIcon()
    {
        try
        {
            _tray = new TrayIcon(IconPath(alert: false), "DySS Cockpit", TrayMenu);
            _tray.Activated += (_, _) => ShowWindow();
            _trayShowsAlert = false;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            LogTrayUnavailable(_logger, ex);
        }
    }

    private IReadOnlyList<TrayMenuItem> TrayMenu() =>
    [
        new(T("Ouvrir DySS Cockpit", "Open DySS Cockpit"), ShowWindow),
        new(T("Actualiser", "Refresh"), () => _shell?.ViewModel.RefreshCommand.Execute(null), _shell is not null),
        TrayMenuItem.Separator,
        new(T("Quitter", "Quit"), Quit),
    ];

    /// <summary>The robot in the icon's tooltip, and a red badge on it while the robot reports a fault.</summary>
    private void UpdateTray()
    {
        if (_tray is null) return;
        var alert = _shell?.ViewModel.NeedsAttention == true;
        _tray.Update(alert != _trayShowsAlert ? IconPath(alert) : null,
            _shell is { } shell ? $"DySS Cockpit\n{shell.ViewModel.Summary}" : "DySS Cockpit");
        _trayShowsAlert = alert;
    }

    /// <summary>Quits for good, from the icon's menu or the Quit button: the window closes for real, which ends the session and the process.</summary>
    internal void Quit()
    {
        if (_exiting) return;
        _exiting = true;
        _tray?.Dispose();
        _tray = null;
        _window?.Close();
    }

    /// <summary>
    /// Starts the application again, for a new language. The icon goes first, the process being
    /// ended without its window closing; the new instance waits for this one to be gone before
    /// taking its place. Only comes back when Windows could not restart it, the icon then put back.
    /// </summary>
    internal Windows.ApplicationModel.Core.AppRestartFailureReason Restart()
    {
        _tray?.Dispose();
        _tray = null;
        var failure = AppInstance.Restart($"{Program.WaitForExitArgument}={Environment.ProcessId}");
        CreateTrayIcon();
        UpdateTray();
        return failure;
    }
}
