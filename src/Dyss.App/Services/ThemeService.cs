using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using Dyss.App.Rendering;
using Dyss.Presentation.Map;

namespace Dyss.App.Services;

/// <summary>
/// Follows the Windows "app mode" (light or dark) and swaps the brush dictionary the XAML binds to
/// with DynamicResource. Re-applies when the user changes the setting while the app runs.
/// </summary>
public static class ThemeService
{
    public static bool IsDark { get; private set; }
    public static event Action? Changed;

    private static ResourceDictionary? _current;

    public static void Start()
    {
        Apply(ReadWindowsPrefersDark());
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle)
                Application.Current.Dispatcher.BeginInvoke(() => Apply(ReadWindowsPrefersDark()));
        };
    }

    /// <summary>HKCU\...\Themes\Personalize\AppsUseLightTheme: 0 means dark. Missing means light.</summary>
    private static bool ReadWindowsPrefersDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch { return false; }
    }

    public static void Apply(bool dark)
    {
        if (_current is not null && dark == IsDark) return;
        IsDark = dark;
        var dict = dark ? Dark() : Light();
        var merged = Application.Current.Resources.MergedDictionaries;
        if (_current is not null) merged.Remove(_current);
        merged.Add(dict);
        _current = dict;
        MapRenderer.Palette = dark ? MapPalette.Dark : MapPalette.Light;
        Changed?.Invoke();
    }

    private static ResourceDictionary Dark() => Build(
        bg: "#1e1e22", panel: "#26262c", card: "#2d2d34", input: "#2a2a30", border: "#3c3c44",
        text: "#f2f2f2", muted: "#9a9aa4", accent: "#7c5cff", accentText: "#ffffff",
        selection: "#3a3a7a", danger: "#e05050", success: "#3cb43c", disabled: "#3a3a40", disabledText: "#808088",
        hoverOverlay: "#1affffff", pressedOverlay: "#33ffffff");

    private static ResourceDictionary Light() => Build(
        bg: "#f6f6f8", panel: "#ffffff", card: "#eeeef2", input: "#ffffff", border: "#d0d0d8",
        text: "#1a1a1e", muted: "#5c5c66", accent: "#5b3ddf", accentText: "#ffffff",
        selection: "#d6cffa", danger: "#c83c3c", success: "#2e9e2e", disabled: "#e2e2e6", disabledText: "#9a9aa4",
        hoverOverlay: "#14000000", pressedOverlay: "#29000000");

    private static ResourceDictionary Build(string bg, string panel, string card, string input, string border,
        string text, string muted, string accent, string accentText, string selection, string danger, string success,
        string disabled, string disabledText, string hoverOverlay, string pressedOverlay)
    {
        var d = new ResourceDictionary();
        void Add(string key, string hex)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)!);
            brush.Freeze();
            d[key] = brush;
        }
        Add("BgBrush", bg); Add("PanelBrush", panel); Add("CardBrush", card); Add("InputBrush", input);
        Add("BorderBrush", border); Add("TextBrush", text); Add("MutedBrush", muted); Add("AccentBrush", accent);
        Add("AccentTextBrush", accentText); Add("SelectionBrush", selection); Add("DangerBrush", danger);
        Add("SuccessBrush", success); Add("DisabledBrush", disabled); Add("DisabledTextBrush", disabledText);
        // Translucent layers drawn over a control on hover and press, so one pair works on the
        // accent and on the card backgrounds alike: white lightens in the dark theme, black darkens
        // in the light one.
        Add("HoverOverlayBrush", hoverOverlay); Add("PressedOverlayBrush", pressedOverlay);
        return d;
    }
}
