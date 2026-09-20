using System.Windows;
using MyDyson.App.Services;
using MyDyson.App.ViewModels;

namespace MyDyson.App.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly NotificationService _notifications = new();

    /// <summary>Raised when the user logs out from within the window, just before it closes.</summary>
    public event Action? LoggedOut;

    public MainWindow(RobotContext ctx)
    {
        InitializeComponent();
        _vm = new MainViewModel(ctx);
        DataContext = _vm;
        MapCanvas.ZoneClicked += _vm.ToggleZone;
        _vm.LoggedOut += () => { LoggedOut?.Invoke(); Close(); };
        _vm.NotifyRequested += (title, text) => _notifications.Show(title, text, onClick: () =>
        {
            Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
            SelectTab(1);
        });
        Loaded += async (_, _) => await _vm.StartAsync();
        Closing += async (_, _) => await _vm.ShutdownAsync();
        Closed += (_, _) => _notifications.Dispose();
    }

    private void ResetZoom_Click(object sender, RoutedEventArgs e) => MapCanvas.ResetView();
    private void ResetHistoryZoom_Click(object sender, RoutedEventArgs e) => HistoryCanvas.ResetView();

    public void ClickZone(string zoneId) => _vm.ToggleZone(zoneId);

    public void SelectTab(int index)
    {
        if (index >= 0 && index < Tabs.Items.Count) Tabs.SelectedIndex = index;
        if (index == 1) _vm.SelectFirstClean();
    }

    /// <summary>Renders the window content to a PNG, for documentation and for checking the layout without a screen.</summary>
    public void SaveScreenshot(string path)
    {
        var root = (FrameworkElement)Content;
        var w = (int)Math.Ceiling(root.ActualWidth);
        var h = (int)Math.Ceiling(root.ActualHeight);
        // Paint the window background first: the content alone leaves transparent areas, which
        // come out black in the PNG.
        var visual = new System.Windows.Media.DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Background, null, new Rect(0, 0, w, h));
            dc.DrawRectangle(new System.Windows.Media.VisualBrush(root), null, new Rect(0, 0, w, h));
        }
        var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(w, h, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        rtb.Render(visual);
        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
        using var fs = System.IO.File.Create(path);
        enc.Save(fs);
    }
}
