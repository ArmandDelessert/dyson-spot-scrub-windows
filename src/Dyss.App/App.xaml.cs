using Dyss.App.Rendering;
using Dyss.App.Services;
using Dyss.App.Views;
using Dyss.Core;
using Dyss.Presentation.Map;
using Dyss.Presentation.Services;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using static Dyss.Core.Translation;

namespace Dyss.App;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "The application's lifetime is its process's: the notifications are disposed when the window closes, the log when the process exits.")]
public partial class App : Application
{
    /// <summary>The log on disk, %APPDATA%\DySS Cockpit\journal-2026-10-05.log: one file a day, kept a week.</summary>
    private readonly FileLoggerProvider _logFile;
    private readonly ILoggerFactory _loggers;
    private readonly ILogger _logger;
    private MainWindow? _window;
    private NotificationService? _notifications;
    /// <summary>The dashboard, while it is on show.</summary>
    private ShellView? _shell;

    public App()
    {
        // The language comes first: the XAML resources loaded just below already carry texts.
        Translation.Current = Option(Environment.GetCommandLineArgs(), "--lang") switch
        {
            "fr" => AppLanguage.French,
            "en" => AppLanguage.English,
            _ => DisplaySettings.Load().ChosenLanguage,
        };
        _logFile = new FileLoggerProvider(SessionStore.Directory);
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

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        var argv = Environment.GetCommandLineArgs().Skip(1).ToArray();
        try
        {
            await RunAsync(argv);
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

    private static string? Option(string[] args, string name) =>
        Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;

    private async Task RunAsync(string[] args)
    {
        // --theme light|dark forces a theme, mainly for screenshots and exports.
        var theme = Option(args, "--theme") switch
        {
            "light" => ElementTheme.Light,
            "dark" => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };

        // Headless helper: render a map to a PNG and exit. Used to check the renderer without a
        // window, and handy for sharing a map. Defaults to the current map.
        //   DyssCockpit.exe --export-map out.png [--serial S] [--map-id ID] [--theme light]
        if (Option(args, "--export-map") is { } exportPath)
        {
            // Headless: a failure goes to the console and the exit code, not to a dialog nobody
            // may be there to close.
            try { Environment.ExitCode = await ExportMapAsync(exportPath, Option(args, "--serial"), Option(args, "--map-id"), theme, _loggers); }
            catch (Exception ex)
            {
                Console.Error.WriteLine(T($"Export impossible : {ex.Message}", $"Export failed: {ex.Message}"));
                Environment.ExitCode = 1;
            }
            Exit();
            return;
        }

        // Headless helper: writes the application icon, drawn by the map's own code, to an .ico.
        //   DyssCockpit.exe --export-icon app.ico
        if (Option(args, "--export-icon") is { } iconPath)
        {
            await using (var file = File.Create(iconPath))
                await AppIcon.WriteIcoAsync(file, AppIcon.Sizes);
            Exit();
            return;
        }

        _window = new MainWindow();
        if (theme != ElementTheme.Default) _window.ForceTheme(theme);
        _window.Activate();
        _notifications = new NotificationService(_window.DispatcherQueue, _loggers.CreateLogger<NotificationService>());
        _window.Closed += (_, _) => _notifications.Dispose();
        if (Option(args, "--screenshot") is { } shotPath) _ = ScreenshotAsync(args, shotPath);

        // --login starts at the login whatever session is stored, mainly for screenshots: the stored
        // one stays until another login replaces it.
        var skipStored = Array.IndexOf(args, "--login") >= 0;

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

            _shell = new ShellView(_window, ctx, _notifications);
            _window.Show(_shell);
            var loggedOut = await _shell.Finished;
            _shell = null;
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
        if (_window?.Content?.XamlRoot is not { } root) return false;
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

    private static async Task<int> ExportMapAsync(string path, string? serial, string? mapId, ElementTheme theme, ILoggerFactory loggers)
    {
        var ctx = RobotContext.FromStoredSession(loggers);
        if (ctx is null) { Console.Error.WriteLine(T("Aucune session. Lancez l'application et connectez-vous d'abord.", "No session. Start the application and log in first.")); return 2; }
        await using var _ = ctx;
        var robot = await ctx.LoadDevicesAsync(serial);
        if (robot is null) { Console.Error.WriteLine(T("Robot introuvable.", "Robot not found.")); return 2; }
        var s = robot.SerialNumber;

        var maps = await ctx.Api.GetMapMetadataAsync(s);
        var current = mapId is not null
            ? maps.FirstOrDefault(m => m.Id == mapId) ?? throw new InvalidOperationException(T($"Carte {mapId} introuvable.", $"Map {mapId} not found."))
            : maps.FirstOrDefault(m => m.IsCurrentMap) ?? maps.First();
        var map = await ctx.Api.GetPersistentMapAsync(s, current.Id);
        // The occupancy grid and the live robot position/path only ever describe the currently
        // active map; attaching them to another map would overlay an unrelated task's stray path.
        MapGrid? grid = null; RobotPosition? robotPos = null; List<Dyss.Core.Point>? cleanPath = null; List<Dyss.Core.Point>? obstacles = null; List<DirtSpot>? dirt = null;
        if (current.IsCurrentMap)
        {
            grid = MapGrid.From(await ctx.Api.GetMappingMapAsync(s));
            var live = await ctx.Api.GetLiveCleaningMapAsync(s);
            robotPos = live.RobotLocation;
            cleanPath = live.CleanPath;
            obstacles = live.Obstacles;
            dirt = live.Dirt;
        }

        var scene = new MapScene
        {
            Grid = grid, Map = map, ZoneMetadata = current.Zones,
            Dock = map.DockLocation, Robot = robotPos, Path = cleanPath, Obstacles = obstacles, DirtSpots = dirt,
        };
        await MapImage.ExportPngAsync(scene, 1200, 1400, path, theme == ElementTheme.Light ? MapPalette.Light : MapPalette.Dark);
        Console.WriteLine(T($"Carte {current.Name} exportée vers {path}", $"Map {current.Name} exported to {path}"));
        return 0;
    }

    /// <summary>
    /// --screenshot out.png [--after 20]: renders the window once data has arrived — or whatever it
    /// shows by then, the login for one — then exits. On the dashboard, --tab 1 shows another page,
    /// --zones 11,10 clicks rooms in that order, --manage-maps shoots the map manager instead
    /// (--layer 1 or 2 on the zones or furniture tab, --map-id on another map), and --edit-schedule
    /// the editor of a new schedule.
    /// </summary>
    private async Task ScreenshotAsync(string[] args, string path)
    {
        try
        {
            var seconds = int.TryParse(Option(args, "--after"), out var n) ? n : 20;
            await Task.Delay(TimeSpan.FromSeconds(seconds));
            UIElement shot = _window!.View;
            if (_shell is { } shell)
            {
                foreach (var zone in Option(args, "--zones")?.Split(',') ?? []) shell.ClickZone(zone);
                shell.SelectTab(int.TryParse(Option(args, "--tab"), out var tab) ? tab : 0);
                await Task.Delay(500);
                if (Array.IndexOf(args, "--manage-maps") >= 0)
                {
                    shell.OpenMapManagerForScreenshot(int.TryParse(Option(args, "--layer"), out var layer) ? layer : 0, Option(args, "--map-id"));
                    await Task.Delay(TimeSpan.FromSeconds(6));
                }
                else if (Array.IndexOf(args, "--edit-schedule") >= 0 && shell.OpenScheduleEditorForScreenshot() is { } dialog)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2));
                    shot = dialog;
                }
            }
            await Screenshot.SaveAsync(shot, path, _window.Theme);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"--screenshot failed: {ex}");
        }
        // A diagnostic one-shot process has no reason to wait for a graceful close or for the
        // broker to say goodbye: the PNG is already on disk.
        Environment.Exit(0);
    }
}
