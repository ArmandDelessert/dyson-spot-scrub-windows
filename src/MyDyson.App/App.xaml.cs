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
        ThemeService.Start();
        // --theme light|dark forces a theme, mainly for screenshots.
        var themeIdx = Array.IndexOf(e.Args, "--theme");
        if (themeIdx >= 0 && themeIdx + 1 < e.Args.Length)
            ThemeService.Apply(e.Args[themeIdx + 1].Equals("dark", StringComparison.OrdinalIgnoreCase));

        // Headless helper: render the current map to a PNG and exit. Used to check the renderer
        // without a window, and handy for sharing a map.
        //   MyDyson.App.exe --export-map out.png [--serial S]
        var args = e.Args;
        var export = Array.IndexOf(args, "--export-map");
        if (export >= 0 && export + 1 < args.Length)
        {
            var serialIdx = Array.IndexOf(args, "--serial");
            var serial = serialIdx >= 0 && serialIdx + 1 < args.Length ? args[serialIdx + 1] : null;
            Environment.ExitCode = await ExportMapAsync(args[export + 1], serial);
            Shutdown();
            return;
        }

        // Loop so that logging out from the main window (see MainViewModel.LogoutCommand) returns
        // here to sign in again, instead of only being able to do that once at process start.
        while (true)
        {
            var ctx = RobotContext.FromStoredSession();
            if (ctx is null)
            {
                var login = new LoginWindow();
                if (login.ShowDialog() != true || login.Result is null)
                {
                    Shutdown();
                    return;
                }
                ctx = login.Result;
            }

            try
            {
                var robot = await ctx.LoadDevicesAsync();
                if (robot is null)
                {
                    MessageBox.Show("Aucun appareil sur ce compte.", "MyDyson", MessageBoxButton.OK, MessageBoxImage.Warning);
                    Shutdown();
                    return;
                }
            }
            catch (DysonAuthException)
            {
                // Token expired: log in again.
                SessionStore.Delete();
                await ctx.DisposeAsync();
                var login = new LoginWindow();
                if (login.ShowDialog() != true || login.Result is null) { Shutdown(); return; }
                ctx = login.Result;
                await ctx.LoadDevicesAsync();
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
                    await Dispatcher.InvokeAsync(() => main.SaveScreenshot(args[shot + 1]));
                    Shutdown();
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

    private static async Task<int> ExportMapAsync(string path, string? serial)
    {
        var ctx = RobotContext.FromStoredSession();
        if (ctx is null) { Console.Error.WriteLine("Aucune session. Lancez l'application et connectez-vous d'abord."); return 2; }
        var robot = await ctx.LoadDevicesAsync(serial);
        if (robot is null) { Console.Error.WriteLine("Robot introuvable."); return 2; }
        var s = robot.SerialNumber;

        var maps = await ctx.Api.GetMapMetadataAsync(s);
        var current = maps.FirstOrDefault(m => m.IsCurrentMap) ?? maps.First();
        var map = await ctx.Api.GetPersistentMapAsync(s, current.Id);
        var grid = MapGrid.From(await ctx.Api.GetMappingMapAsync(s));
        var live = await ctx.Api.GetLiveCleaningMapAsync(s);

        var scene = new MapScene
        {
            Grid = grid, Map = map, ZoneMetadata = current.Zones,
            Dock = map.DockLocation, Robot = live.RobotLocation, Path = live.CleanPath,
        };
        MapRenderer.ExportPng(scene, 1200, 1400, path);
        Console.WriteLine($"Carte {current.Name} exportée vers {path}");
        await ctx.DisposeAsync();
        return 0;
    }
}
