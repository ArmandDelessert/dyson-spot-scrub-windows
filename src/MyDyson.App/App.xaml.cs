using System.Windows;
using MyDyson.App.Rendering;
using MyDyson.App.Services;
using MyDyson.App.Views;
using MyDyson.Core;

namespace MyDyson.App;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // Last resort for anything a command or dispatcher callback lets escape: tell the user and
        // keep the window open rather than vanishing without a word. Real bugs still surface — as a
        // message box instead of a crash, and in the error log — so this hides nothing, it just
        // doesn't lose the session.
        DispatcherUnhandledException += (_, args) =>
        {
            Console.Error.WriteLine(args.Exception);
            ErrorLog.Write("interface", args.Exception);
            MessageBox.Show(args.Exception.Message, "Erreur inattendue", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        // Off the UI thread nothing can be saved any more — the process is going down — but the
        // log at least says why.
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex) ErrorLog.Write("arrêt brutal", ex);
        };
        // A forgotten task that failed: harmless to the process, but worth a trace.
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            ErrorLog.Write("tâche", args.Exception);
            args.SetObserved();
        };

        // The app is set to shut down explicitly (a login window closing must not end it), so
        // anything escaping start-up would otherwise leave a process running with no window and
        // nothing to close — which is what an unreachable network used to do.
        try
        {
            await RunAsync(e.Args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            ErrorLog.Write("démarrage", ex);
            MessageBox.Show($"L'application n'a pas pu démarrer : {ex.Message}", "Dyson Spot+Scrub AI", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private async Task RunAsync(string[] args)
    {
        ThemeService.Start();
        // --theme light|dark forces a theme, mainly for screenshots.
        var themeIdx = Array.IndexOf(args, "--theme");
        if (themeIdx >= 0 && themeIdx + 1 < args.Length)
            ThemeService.Apply(args[themeIdx + 1].Equals("dark", StringComparison.OrdinalIgnoreCase));

        // Headless helper: render a map to a PNG and exit. Used to check the renderer without a
        // window, and handy for sharing a map. Defaults to the current map.
        //   MyDyson.App.exe --export-map out.png [--serial S] [--map-id ID]
        var export = Array.IndexOf(args, "--export-map");
        if (export >= 0 && export + 1 < args.Length)
        {
            var serialIdx = Array.IndexOf(args, "--serial");
            var serial = serialIdx >= 0 && serialIdx + 1 < args.Length ? args[serialIdx + 1] : null;
            var mapIdIdx = Array.IndexOf(args, "--map-id");
            var mapId = mapIdIdx >= 0 && mapIdIdx + 1 < args.Length ? args[mapIdIdx + 1] : null;
            // Headless: a failure goes to the console and the exit code, not to a message box
            // nobody may be there to close.
            try { Environment.ExitCode = await ExportMapAsync(args[export + 1], serial, mapId); }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Export impossible : {ex.Message}");
                Environment.ExitCode = 1;
            }
            Shutdown();
            return;
        }

        // Headless helper: writes the application icon, drawn by the map's own code, to an .ico.
        //   MyDyson.App.exe --export-icon app.ico
        var icon = Array.IndexOf(args, "--export-icon");
        if (icon >= 0 && icon + 1 < args.Length)
        {
            using (var file = System.IO.File.Create(args[icon + 1]))
                Rendering.AppIcon.WriteIco(file, Rendering.AppIcon.Sizes);
            Shutdown();
            return;
        }

        // Loop so that logging out from the main window (see MainViewModel.LogoutCommand) returns
        // here to sign in again, instead of only being able to do that once at process start.
        while (true)
        {
            var ctx = await OpenAccountAsync(RobotContext.FromStoredSession());
            if (ctx is null)
            {
                Shutdown();
                return;
            }

            var main = new MainWindow(ctx);
            MainWindow = main;
            var loggedOut = false;
            main.LoggedOut += () => loggedOut = true;
            var closed = new TaskCompletionSource();
            main.Closed += (_, _) => closed.TrySetResult();
            main.Show();

            // --screenshot out.png [--after 20]: capture the main window once data has arrived, then
            // exit. Only meaningful on the first pass; Shutdown() below ends the process regardless.
            var shot = Array.IndexOf(args, "--screenshot");
            if (shot >= 0 && shot + 1 < args.Length)
            {
                var afterIdx = Array.IndexOf(args, "--after");
                var seconds = afterIdx >= 0 && afterIdx + 1 < args.Length && int.TryParse(args[afterIdx + 1], out var n) ? n : 20;
                var tabIdx = Array.IndexOf(args, "--tab");
                var tab = tabIdx >= 0 && tabIdx + 1 < args.Length && int.TryParse(args[tabIdx + 1], out var t) ? t : 0;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(seconds));
                        // --zones 11,10 simulates clicks on rooms, in that order.
                        var zonesIdx = Array.IndexOf(args, "--zones");
                        await Dispatcher.InvokeAsync(() =>
                        {
                            if (zonesIdx >= 0 && zonesIdx + 1 < args.Length)
                                foreach (var z in args[zonesIdx + 1].Split(',')) main.ClickZone(z);
                            main.SelectTab(tab);
                        });
                        await Task.Delay(500);
                        // --manage-maps shoots the map manager instead of the dashboard, that window
                        // being reachable no other way without a person to click the button.
                        // --layer 1 or 2 opens it on the zones or the furniture tab, --map-id on another map.
                        if (Array.IndexOf(args, "--manage-maps") >= 0)
                        {
                            var layerIdx = Array.IndexOf(args, "--layer");
                            var layer = layerIdx >= 0 && layerIdx + 1 < args.Length && int.TryParse(args[layerIdx + 1], out var l) ? l : 0;
                            var mapIdx = Array.IndexOf(args, "--map-id");
                            var onMap = mapIdx >= 0 && mapIdx + 1 < args.Length ? args[mapIdx + 1] : null;
                            await Dispatcher.InvokeAsync(() => main.OpenMapManagerForScreenshot(layer, onMap));
                            await Task.Delay(TimeSpan.FromSeconds(6));
                            await Dispatcher.InvokeAsync(() => main.SaveMapManagerScreenshot(args[shot + 1]));
                        }
                        // --edit-schedule, likewise, shoots the editor of a new schedule.
                        else if (Array.IndexOf(args, "--edit-schedule") >= 0)
                        {
                            await Dispatcher.InvokeAsync(() => main.OpenScheduleEditorForScreenshot());
                            await Task.Delay(TimeSpan.FromSeconds(2));
                            await Dispatcher.InvokeAsync(() => main.SaveScheduleEditorScreenshot(args[shot + 1]));
                        }
                        else
                        {
                            await Dispatcher.InvokeAsync(() => main.SaveScreenshot(args[shot + 1]));
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"--screenshot failed: {ex}");
                    }
                    // A diagnostic one-shot process has no reason to wait for a graceful window
                    // close or MQTT teardown (observed to sometimes hang for minutes against this
                    // broker): the PNG is already on disk, so exit immediately regardless.
                    Environment.Exit(0);
                });
            }

            await closed.Task;
            if (!loggedOut)
            {
                Shutdown();
                return;
            }
            // else: loop back around and show the login window again.
        }
    }

    /// <summary>
    /// Gets as far as a robot to show: logs in when there is no session or the stored one is
    /// refused, and waits for the network when Dyson cannot be reached. Null when the user gives
    /// up (closes the login, quits the wait) or the account has no device.
    /// </summary>
    private static async Task<RobotContext?> OpenAccountAsync(RobotContext? ctx)
    {
        while (true)
        {
            if (ctx is null)
            {
                var login = new LoginWindow();
                if (login.ShowDialog() != true || login.Result is null) return null;
                ctx = login.Result;
            }

            try
            {
                if (await ctx.LoadDevicesAsync() is not null) return ctx;
                MessageBox.Show("Aucun appareil sur ce compte.", "Dyson Spot+Scrub AI", MessageBoxButton.OK, MessageBoxImage.Warning);
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
                ErrorLog.Write("appareils", ex);
                if (!ConnectionWaitWindow.Ask(ex.Message))
                {
                    await ctx.DisposeAsync();
                    return null;
                }
            }
        }
    }

    private static async Task<int> ExportMapAsync(string path, string? serial, string? mapId = null)
    {
        var ctx = RobotContext.FromStoredSession();
        if (ctx is null) { Console.Error.WriteLine("Aucune session. Lancez l'application et connectez-vous d'abord."); return 2; }
        var robot = await ctx.LoadDevicesAsync(serial);
        if (robot is null) { Console.Error.WriteLine("Robot introuvable."); return 2; }
        var s = robot.SerialNumber;

        var maps = await ctx.Api.GetMapMetadataAsync(s);
        var current = mapId is not null
            ? maps.FirstOrDefault(m => m.Id == mapId) ?? throw new InvalidOperationException($"Carte {mapId} introuvable.")
            : maps.FirstOrDefault(m => m.IsCurrentMap) ?? maps.First();
        var map = await ctx.Api.GetPersistentMapAsync(s, current.Id);
        // The occupancy grid and the live robot position/path only ever describe the currently
        // active map; attaching them to another map would overlay an unrelated task's stray path.
        MapGrid? grid = null; RobotPosition? robotPos = null; List<MyDyson.Core.Point>? cleanPath = null; List<MyDyson.Core.Point>? obstacles = null; List<DirtSpot>? dirt = null;
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
        MapRenderer.ExportPng(scene, 1200, 1400, path);
        Console.WriteLine($"Carte {current.Name} exportée vers {path}");
        await ctx.DisposeAsync();
        return 0;
    }
}
