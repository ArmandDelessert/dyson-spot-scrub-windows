using System.Runtime.InteropServices;
using DyssCockpit.Presentation.Services;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;

namespace DyssCockpit.App.Views;

/// <summary>
/// The application's one window: a Mica backdrop under its own title bar, and whichever view the
/// application is at — login, waiting for the network, the dashboard.
/// </summary>
public sealed partial class MainWindow : Window
{
    private readonly AppSettings? _settings;
    private readonly DispatcherQueueTimer _saveTimer;

    /// <param name="settings">Where the window's place is kept; null for a window that neither takes the last one nor leaves one (a screenshot).</param>
    public MainWindow(AppSettings? settings = null)
    {
        _settings = settings;
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));

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

        // Where it was left, if that is still on a screen; remembered a moment after each move or resize, and on closing.
        _saveTimer = DispatcherQueue.CreateTimer();
        _saveTimer.Interval = TimeSpan.FromSeconds(1);
        _saveTimer.IsRepeating = false;
        _saveTimer.Tick += (_, _) => SaveBounds();
        if (_settings is null) return;
        RestoreBounds();
        AppWindow.Changed += (_, args) =>
        {
            if (args.DidPositionChange || args.DidSizeChange) _saveTimer.Start();
        };
        AppWindow.Closing += (_, _) => SaveBounds();
    }

    private void RestoreBounds()
    {
        if (_settings?.Window is not { } bounds || !IsOnScreen(bounds)) return;
        AppWindow.MoveAndResize(new RectInt32(bounds.X, bounds.Y, bounds.Width, bounds.Height));
        if (bounds.Maximized && AppWindow.Presenter is OverlappedPresenter presenter) presenter.Maximize();
    }

    /// <summary>
    /// Remembers the window as it is; maximized, the size it had before is kept, so that restoring it
    /// next time goes back to that. Nothing while it is minimized or hidden in the notification area.
    /// </summary>
    private void SaveBounds()
    {
        if (_settings is null || AppWindow.Presenter is not OverlappedPresenter presenter
            || presenter.State == OverlappedPresenterState.Minimized || !AppWindow.IsVisible)
            return;
        var maximized = presenter.State == OverlappedPresenterState.Maximized;
        var previous = _settings.Window;
        _settings.RememberWindow(maximized && previous is not null
            ? previous with { Maximized = true }
            : new WindowBounds(AppWindow.Position.X, AppWindow.Position.Y, AppWindow.Size.Width, AppWindow.Size.Height, maximized));
    }

    private static bool IsOnScreen(WindowBounds bounds)
    {
        // DisplayArea.FindAll() must be accessed by index: enumerating it throws InvalidCastException.
        var displays = DisplayArea.FindAll();
        for (var i = 0; i < displays.Count; i++)
        {
            var area = displays[i].WorkArea;
            if (bounds.Width > 200 && bounds.Height > 200
                && bounds.X + 50 >= area.X && bounds.Y >= area.Y - 10
                && bounds.X + 100 <= area.X + area.Width && bounds.Y + 50 <= area.Y + area.Height)
                return true;
        }
        return false;
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
