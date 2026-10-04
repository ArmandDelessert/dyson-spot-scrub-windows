using Dyss.App.Services;
using Dyss.Core;
using Dyss.Presentation.Services;
using Dyss.Presentation.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

using static Dyss.Core.Translation;

namespace Dyss.App.Views;

/// <summary>
/// The dashboard once logged in: owns the <see cref="MainViewModel"/> for the session, and shows its
/// pages in a navigation view — the map manager among them, with the title bar's back button to
/// leave it. Ends when the user logs out or the window closes; see <see cref="Finished"/>.
/// </summary>
public sealed partial class ShellView : UserControl
{
    private readonly MainWindow _window;
    private readonly NotificationService _notifications;
    private readonly TaskCompletionSource<bool> _finished = new();
    private readonly DashboardView _dashboard;
    private readonly HistoryView _history;
    private readonly SchedulesView _schedules;
    private readonly SettingsView _settings;
    private readonly JournalView _journal;
    private MapManagerView? _mapManager;
    /// <summary>The page to go back to when the map manager closes.</summary>
    private NavigationViewItem? _beforeMaps;
    private bool _started;

    internal ShellView(MainWindow window, RobotContext ctx, NotificationService notifications)
    {
        _window = window;
        _notifications = notifications;
        ViewModel = new MainViewModel(ctx, new WinUiDispatcher(DispatcherQueue), new WinUiDialogService(window));
        InitializeComponent();

        _dashboard = new DashboardView(ViewModel);
        _history = new HistoryView(ViewModel.History);
        _schedules = new SchedulesView(ViewModel.Schedules);
        _settings = new SettingsView(ViewModel.Settings);
        _journal = new JournalView(ViewModel.Journal);

        ViewModel.LoggedOut += () =>
        {
            Close();
            _finished.TrySetResult(true);
        };
        ViewModel.NotifyRequested += (title, text) => _notifications.Show(title, text, onClick: () =>
        {
            _window.BringToFront();
            SelectTab(1);
        });
        ViewModel.MapManagerRequested += () => Nav.SelectedItem = MapsItem;
        _window.Closed += OnWindowClosed;
        _window.TitleBar.PaneToggleRequested += OnPaneToggleRequested;
        _window.TitleBar.BackRequested += OnBackRequested;

        Loaded += async (_, _) =>
        {
            _window.TitleBar.IsPaneToggleButtonVisible = true;
            if (_started) return;
            _started = true;
            await ViewModel.StartAsync();
        };
        Nav.SelectedItem = DashboardItem;
        (ViewModel.Display.Language switch { "fr" => LanguageFrench, "en" => LanguageEnglish, _ => LanguageAuto }).IsChecked = true;
        PreviewKeyDown += OnPreviewKeyDown;
        KeyDown += OnKeyDown;
    }

    public MainViewModel ViewModel { get; }

    /// <summary>True when the user logged out (the login comes back), false when the window closed.</summary>
    public Task<bool> Finished => _finished.Task;

    // ---- Pages -----------------------------------------------------------------------------

    /// <summary>
    /// Keeps the language chosen in the header menu. Every text is laid out in it once, as the
    /// window is built, so a different language takes a restart, offered straight away.
    /// </summary>
    private async void Language_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioMenuFlyoutItem { Tag: string language } || language == ViewModel.Display.Language) return;
        ViewModel.Display.Language = language;
        if (ViewModel.Display.ChosenLanguage == Dyss.Core.Translation.Current) return;
        if (!await ViewModel.Hub.Dialogs.ConfirmAsync(T("Changer de langue", "Change language"),
                T("La nouvelle langue s'applique au redémarrage de l'application. Redémarrer maintenant ?",
                  "The new language applies when the application restarts. Restart now?"))) return;
        // Only comes back when Windows could not restart the application.
        var failure = Microsoft.Windows.AppLifecycle.AppInstance.Restart("");
        await ViewModel.Hub.Dialogs.AlertAsync(T("Changer de langue", "Change language"),
            T($"Le redémarrage a échoué ({failure}). Relancez l'application pour changer de langue.",
              $"The restart failed ({failure}). Start the application again to change its language."));
    }

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem item) return;
        if (item != MapsItem) CloseMapManager();
        Page.Content = item.Tag switch
        {
            "history" => _history,
            "schedules" => _schedules,
            "settings" => _settings,
            "journal" => _journal,
            "maps" => OpenMapManager(),
            _ => _dashboard,
        };
        if (item != MapsItem) _beforeMaps = item;
        _window.TitleBar.IsBackButtonVisible = item == MapsItem;
    }

    /// <summary>The map manager works off its own copy of the map, so it is built afresh each time it opens, on the map the dashboard shows.</summary>
    private MapManagerView OpenMapManager()
    {
        if (_mapManager is not null) return _mapManager;
        var vm = ViewModel.CreateMapManager();
        // Renames, merges and splits change what the dashboard lists.
        vm.Changed += () => _ = ViewModel.ReloadMapsAsync();
        return _mapManager = new MapManagerView(vm, ViewModel.Cleaning.SelectedMap?.Id);
    }

    private void CloseMapManager()
    {
        _mapManager?.Map.Release();
        _mapManager = null;
    }

    private void OnBackRequested(TitleBar sender, object args) => Nav.SelectedItem = _beforeMaps ?? DashboardItem;

    private void OnPaneToggleRequested(TitleBar sender, object args) => Nav.IsPaneOpen = !Nav.IsPaneOpen;

    /// <summary>0 the dashboard, 1 the history (its latest clean chosen), then the schedules, the settings, the journal.</summary>
    public void SelectTab(int index)
    {
        NavigationViewItem[] items = [DashboardItem, HistoryItem, SchedulesItem, SettingsItem, JournalItem];
        Nav.SelectedItem = items[Math.Clamp(index, 0, items.Length - 1)];
        if (index == 1) ViewModel.History.SelectFirstClean();
    }

    private void Message_Closed(InfoBar sender, object args) => ViewModel.Hub.Message = "";

    // ---- Escape ----------------------------------------------------------------------------

    /// <summary>A shape being dragged is put back first, on the way down, so nothing else can take the key from it.</summary>
    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Escape) return;
        if (ReferenceEquals(Page.Content, _mapManager) && _mapManager?.Cancel() == true) e.Handled = true;
        else if (ReferenceEquals(Page.Content, _dashboard) && _dashboard.Map.CancelGesture()) e.Handled = true;
    }

    /// <summary>On the dashboard, Escape gives up the zone being drawn, or erases the drawn one.</summary>
    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape && ReferenceEquals(Page.Content, _dashboard) && ViewModel.Cleaning.CancelSpot()) e.Handled = true;
    }

    // ---- The end ---------------------------------------------------------------------------

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        // Stops everything at once, then closes the session in the background: the process ends
        // with the window, and a broker slow to say goodbye must not hold it.
        _ = ViewModel.ShutdownAsync();
        Close();
        _finished.TrySetResult(false);
    }

    /// <summary>Lets go of the window and of what the dashboard owns.</summary>
    private void Close()
    {
        _window.Closed -= OnWindowClosed;
        _window.TitleBar.PaneToggleRequested -= OnPaneToggleRequested;
        _window.TitleBar.BackRequested -= OnBackRequested;
        CloseMapManager();
        ViewModel.Dispose();
    }

    // ---- Diagnostics -----------------------------------------------------------------------

    /// <summary>For screenshots: clicks rooms on the dashboard's map, by id.</summary>
    public void ClickZone(string zoneId) => ViewModel.Cleaning.ToggleZone(zoneId);

    /// <summary>For screenshots: opens the map manager on a tab (0 rooms, 1 zones, 2 furniture), on another map if given.</summary>
    public void OpenMapManagerForScreenshot(int layer, string? mapId)
    {
        var vm = ViewModel.CreateMapManager();
        vm.LayerIndex = layer;
        _mapManager = new MapManagerView(vm, mapId ?? ViewModel.Cleaning.SelectedMap?.Id);
        Nav.SelectedItem = MapsItem;
    }

    /// <summary>
    /// For screenshots: opens the editor of a new schedule, already filled in (Monday and Thursday,
    /// the first two rooms) so the shot shows the estimate and the settings rows. Not awaited.
    /// </summary>
    public ContentDialog? OpenScheduleEditorForScreenshot()
    {
        if (ViewModel.Schedules.NewEditor() is not { } editor) return null;
        editor.Days[0].IsChecked = true;
        editor.Days[3].IsChecked = true;
        foreach (var room in editor.Rooms.Take(2)) room.Selected = true;
        var dialog = new ScheduleEditorDialog(editor);
        new WinUiDialogService(_window).Prepare(dialog);
        _ = dialog.ShowAsync();
        return dialog;
    }
}
