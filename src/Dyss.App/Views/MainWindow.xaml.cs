using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;

namespace Dyss.App.Views;

/// <summary>
/// The application's one window: a Mica backdrop under its own title bar, and whichever view the
/// application is at — login, waiting for the network, the dashboard.
/// </summary>
public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "app.ico"));

        // Sized in pixels: 1240 × 820 at 100 % scaling, centred on the screen it opens on.
        var scale = GetDpiForWindow(Win32Interop.GetWindowFromWindowId(AppWindow.Id)) / 96.0;
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = (int)(960 * scale);
            presenter.PreferredMinimumHeight = (int)(640 * scale);
        }
        var size = new SizeInt32((int)(1240 * scale), (int)(820 * scale));
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        AppWindow.MoveAndResize(new RectInt32(
            area.X + Math.Max(0, (area.Width - size.Width) / 2), area.Y + Math.Max(0, (area.Height - size.Height) / 2),
            Math.Min(size.Width, area.Width), Math.Min(size.Height, area.Height)));
    }

    /// <summary>The title bar, whose pane and back buttons the dashboard drives.</summary>
    public TitleBar TitleBar => AppTitleBar;

    /// <summary>The theme everything is drawn in: the system's, unless one was asked for (see <see cref="ForceTheme"/>).</summary>
    public ElementTheme Theme => Root.ActualTheme;

    /// <summary>Light or dark whatever the system says, for screenshots.</summary>
    public void ForceTheme(ElementTheme theme) => Root.RequestedTheme = theme;

    /// <summary>The window's content, for screenshots.</summary>
    public FrameworkElement View => Root;

    /// <summary>Puts <paramref name="view"/> in the window, in place of whatever was there.</summary>
    public void Show(UIElement view)
    {
        AppTitleBar.IsBackButtonVisible = false;
        AppTitleBar.IsPaneToggleButtonVisible = false;
        Host.Content = view;
    }

    /// <summary>
    /// Ready for a dialog: shown if it was left in the notification area, and its content loaded,
    /// which a window never shown yet — started with Windows — does not have.
    /// </summary>
    public async Task EnsureShownAsync()
    {
        if (!AppWindow.IsVisible) BringToFront();
        if (Root.IsLoaded) return;
        var loaded = new TaskCompletionSource();
        void OnLoaded(object sender, RoutedEventArgs e) => loaded.TrySetResult();
        Root.Loaded += OnLoaded;
        try
        {
            if (!Root.IsLoaded) await loaded.Task;
        }
        finally
        {
            Root.Loaded -= OnLoaded;
        }
    }

    /// <summary>Back to the front: restored if minimised, then activated.</summary>
    public void BringToFront()
    {
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter) presenter.Restore();
        AppWindow.Show();
        Activate();
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
}
