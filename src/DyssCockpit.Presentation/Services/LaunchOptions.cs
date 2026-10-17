using System.Globalization;

namespace DyssCockpit.Presentation.Services;

/// <summary>
/// What the application was started with, read once from its command line. Besides
/// <c>--minimized</c> (started with Windows) and <c>--login</c>, these are diagnostic one-shot runs,
/// documented in the README: render a map or the icon to a file, or a screenshot of the window.
/// Options are <c>--name value</c>; a value that cannot be read leaves the option at its default.
/// </summary>
public sealed record LaunchOptions
{
    /// <summary>Starting with Windows: straight to the notification area, the window left closed.</summary>
    public const string MinimizedArgument = "--minimized";

    public bool Minimized { get; init; }
    /// <summary><c>--login</c>: start at the login whatever session is stored; the stored one stays until another login replaces it.</summary>
    public bool Login { get; init; }
    /// <summary><c>--theme light|dark</c>, mainly for screenshots and exports; null for the system's.</summary>
    public string? Theme { get; init; }
    /// <summary><c>--lang fr|en</c>: the language of this run whatever the settings say; null otherwise.</summary>
    public string? Language { get; init; }

    /// <summary><c>--export-map out.png [--serial S] [--map-id ID]</c>: the map rendered to a PNG, without a window.</summary>
    public string? ExportMap { get; init; }
    public string? Serial { get; init; }
    public string? MapId { get; init; }

    /// <summary><c>--export-icon out.ico [--alert]</c>: the application icon, or with <c>--alert</c> the notification area's while the robot reports a fault.</summary>
    public string? ExportIcon { get; init; }
    public bool Alert { get; init; }

    /// <summary><c>--screenshot out.png</c>: the window rendered to a PNG once data has arrived, then the process ends.</summary>
    public string? Screenshot { get; init; }
    /// <summary><c>--after 20</c>: seconds to wait before the screenshot.</summary>
    public int After { get; init; } = 20;
    /// <summary><c>--zones 11,10</c>: rooms clicked on the dashboard, in that order.</summary>
    public IReadOnlyList<string> Zones { get; init; } = [];
    /// <summary><c>--tab 1</c>: the page shown.</summary>
    public int Tab { get; init; }
    /// <summary><c>--scroll</c>: the page shown scrolled to its end.</summary>
    public bool Scroll { get; init; }
    /// <summary><c>--manage-maps [--layer 1|2]</c>: the map manager, on its rooms, zones or furniture tab, on <see cref="MapId"/> if given.</summary>
    public bool ManageMaps { get; init; }
    public int Layer { get; init; }
    /// <summary><c>--edit-schedule</c>: the editor of a new schedule.</summary>
    public bool EditSchedule { get; init; }

    public bool IsScreenshot => Screenshot is not null;

    /// <summary>A run that does one thing and ends, and so may go alongside the running instance.</summary>
    public bool IsOneShot => ExportMap is not null || ExportIcon is not null || Screenshot is not null;

    /// <summary>Reads the arguments, the executable's own name left out.</summary>
    public static LaunchOptions Parse(IReadOnlyList<string> args)
    {
        string? Value(string name)
        {
            for (var i = 0; i < args.Count - 1; i++)
                if (args[i] == name) return args[i + 1];
            return null;
        }
        bool Flag(string name) => args.Contains(name);
        int Number(string name, int fallback) =>
            int.TryParse(Value(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : fallback;

        return new LaunchOptions
        {
            Minimized = Flag(MinimizedArgument),
            Login = Flag("--login"),
            Theme = Value("--theme") is "light" or "dark" ? Value("--theme") : null,
            Language = Value("--lang") is "fr" or "en" ? Value("--lang") : null,
            ExportMap = Value("--export-map"),
            Serial = Value("--serial"),
            MapId = Value("--map-id"),
            ExportIcon = Value("--export-icon"),
            Alert = Flag("--alert"),
            Screenshot = Value("--screenshot"),
            After = Number("--after", 20),
            Zones = Value("--zones")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [],
            Tab = Number("--tab", 0),
            Scroll = Flag("--scroll"),
            ManageMaps = Flag("--manage-maps"),
            Layer = Number("--layer", 0),
            EditSchedule = Flag("--edit-schedule"),
        };
    }
}
