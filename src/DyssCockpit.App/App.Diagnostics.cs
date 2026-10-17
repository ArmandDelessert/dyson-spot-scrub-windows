using DyssCockpit.App.Rendering;
using DyssCockpit.App.Services;
using DyssCockpit.Core;
using DyssCockpit.Presentation.Map;
using DyssCockpit.Presentation.Services;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;

using static DyssCockpit.Core.Translation;

namespace DyssCockpit.App;

/// <summary>
/// The diagnostic one-shot runs of the application (see <see cref="LaunchOptions"/> and the README):
/// a map or the icon rendered to a file without a window, and a screenshot of the window. Kept
/// apart from the start-up of an ordinary run, which is the rest of <see cref="App"/>.
/// </summary>
public partial class App
{
    /// <summary><c>--theme light|dark</c> forces a theme, mainly for screenshots and exports.</summary>
    private static ElementTheme ThemeOf(LaunchOptions options) => options.Theme switch
    {
        "light" => ElementTheme.Light,
        "dark" => ElementTheme.Dark,
        _ => ElementTheme.Default,
    };

    /// <summary>
    /// <c>--export-map</c> renders a map to a PNG (the current one unless <c>--map-id</c> says otherwise),
    /// to check the renderer without a window or to share a map; <c>--export-icon</c> writes the
    /// application icon, drawn by the map's own code, to an .ico, or with <c>--alert</c> the
    /// notification area's while the robot reports a fault. True when it was one of them: the
    /// process then ends.
    /// </summary>
    private async Task<bool> ExportAsync(LaunchOptions options)
    {
        if (options.ExportMap is { } exportPath)
        {
            // Headless: a failure goes to the console and the exit code, not to a dialog nobody
            // may be there to close.
            try { Environment.ExitCode = await ExportMapAsync(exportPath, options.Serial, options.MapId, ThemeOf(options), _loggers); }
            catch (Exception ex)
            {
                Console.Error.WriteLine(T($"Export impossible : {ex.Message}", $"Export failed: {ex.Message}"));
                Environment.ExitCode = 1;
            }
            Exit();
            return true;
        }
        if (options.ExportIcon is { } iconPath)
        {
            await using (var file = File.Create(iconPath))
                await AppIcon.WriteIcoAsync(file, AppIcon.Sizes, alert: options.Alert);
            Exit();
            return true;
        }
        return false;
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
        MapGrid? grid = null; RobotPosition? robotPos = null; List<DyssCockpit.Core.Point>? cleanPath = null; List<DyssCockpit.Core.Point>? obstacles = null; List<DirtSpot>? dirt = null;
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
    /// (--layer 1 or 2 on the zones or furniture tab, --map-id on another map), --scroll the page shown
    /// scrolled to its end, and --edit-schedule the editor of a new schedule.
    /// </summary>
    private async Task ScreenshotAsync(LaunchOptions options)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(options.After));
            UIElement shot = _window!.View;
            if (_shell is { } shell)
            {
                foreach (var zone in options.Zones) shell.ClickZone(zone);
                shell.SelectTab(options.Tab);
                await Task.Delay(500);
                // --scroll: the page shown, scrolled to its end, for a settings page longer than the window.
                if (options.Scroll)
                {
                    shell.ScrollPageToEnd();
                    await Task.Delay(500);
                }
                if (options.ManageMaps)
                {
                    shell.OpenMapManagerForScreenshot(options.Layer, options.MapId);
                    await Task.Delay(TimeSpan.FromSeconds(6));
                }
                else if (options.EditSchedule && shell.OpenScheduleEditorForScreenshot() is { } dialog)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2));
                    shot = dialog;
                }
            }
            await Screenshot.SaveAsync(shot, options.Screenshot!, _window.Theme);
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
