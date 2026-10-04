using System.Globalization;
using Dyss.App.Rendering;
using Dyss.Presentation.Map;
using Dyss.Presentation.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Dyss.App;

/// <summary>Small conversions for compiled bindings: x:Bind calls functions where classic bindings needed value converters.</summary>
internal static class Ui
{
    public static bool Not(bool value) => !value;

    /// <summary>Shown when false: the opposite of x:Bind's own bool-to-visibility.</summary>
    public static Visibility Unless(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>Shown when there is something to say: a notice that is only there when it is not empty.</summary>
    public static Visibility IfAny(string? text) => string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;

    public static bool HasText(string? text) => !string.IsNullOrEmpty(text);

    public static double Number(int value) => value;

    public static string Percent(int value) => string.Create(CultureInfo.CurrentCulture, $"{value} %");

    public static string Area(double squareMetres) => string.Create(CultureInfo.CurrentCulture, $"({squareMetres:F1} m²)");

    public static string Updated(string time) => string.IsNullOrEmpty(time) ? "" : $"mis à jour {time}";

    public static string SettingsOf(string room) => $"Réglages de {room}";

    public static string ScheduleSwitch(string time) => $"Horaire de {time} activé";

    /// <summary>Only another map than the active one can be made active.</summary>
    public static bool CanSetActive(MapItem? map) => map is { Metadata.IsCurrentMap: false };

    /// <summary>Shown on the map manager's tab <paramref name="tab"/> only.</summary>
    public static Visibility IsLayer(int layer, int tab) => layer == tab ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>A view model's colour as a brush: a restriction zone's swatch.</summary>
    public static Brush Brush(ArgbColor colour) => new SolidColorBrush(colour.ToColor());

    /// <summary>The pause button's icon: play when paused, as it then resumes.</summary>
    public static string PauseGlyph(bool paused) => paused ? "\uE768" : "\uE769";

    /// <summary>The chevron of a room's settings: right when folded, down when open.</summary>
    public static string Chevron(bool expanded) => expanded ? "\uE70D" : "\uE76C";
}
