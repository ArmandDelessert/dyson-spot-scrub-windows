using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MyDyson.App.Services;
using MyDyson.Core;

namespace MyDyson.App.ViewModels;

/// <summary>
/// The dashboard: connects to the robot, routes what it pushes to the tab view models, and owns
/// the session's lifetime (refresh timer, logout, expiry, shutdown). The tabs themselves live in
/// <see cref="StatusViewModel"/>, <see cref="CleaningViewModel"/>, <see cref="HistoryViewModel"/>,
/// <see cref="SchedulesViewModel"/>, <see cref="SettingsViewModel"/> and
/// <see cref="JournalViewModel"/>, sharing a <see cref="RobotHub"/>.
/// </summary>
public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly RobotContext _ctx;
    private readonly DispatcherTimer _refresh;
    private readonly Action _redrawMaps;
    private bool _initialLoadDone;
    private bool _reloading;
    private RobotConnectionStatus _lastStatus = RobotConnectionStatus.Disconnected;
    private readonly MapCatalog _maps;

    public RobotHub Hub { get; }
    public StatusViewModel Status { get; }
    public CleaningViewModel Cleaning { get; }
    public HistoryViewModel History { get; }
    public SettingsViewModel Settings { get; }
    public JournalViewModel Journal { get; }
    public SchedulesViewModel Schedules { get; }
    /// <summary>What this window draws; unlike the Réglages tab, none of it is sent to the robot.</summary>
    public DisplaySettings Display { get; }

    public string RobotName => _ctx.Robot?.Name ?? "Robot";
    public string Serial => _ctx.Robot?.SerialNumber ?? "";
    public string Firmware => _ctx.Robot?.ConnectedConfiguration?.Firmware?.Version ?? "";
    /// <summary>The only account information the login flow ever returns: no display name, just the email used to sign in.</summary>
    public string AccountEmail => _ctx.Stored.Email;

    public event Action? LoggedOut;
    /// <summary>A robot event worth a Windows notification: title, body.</summary>
    public event Action<string, string>? NotifyRequested;

    public MainViewModel(RobotContext ctx)
    {
        _ctx = ctx;
        Hub = new RobotHub(ctx, Application.Current.Dispatcher);
        Display = DisplaySettings.Load();
        _maps = new MapCatalog(Hub);
        Status = new StatusViewModel(Hub);
        Cleaning = new CleaningViewModel(Hub, _maps, Display);
        History = new HistoryViewModel(Hub, _maps, Display);
        Settings = new SettingsViewModel(Hub);
        Journal = new JournalViewModel(Hub, Display);
        Schedules = new SchedulesViewModel(Hub, _maps, () => History.History.Select(c => c.Summary));

        _refresh = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _refresh.Tick += async (_, _) => await Hub.RefreshStateAsync();
        // Both maps bake in the theme and the display preferences, so either changing has to redraw
        // them rather than wait for the next robot message. Kept in a field so Dispose can
        // unsubscribe: ThemeService is static and outlives every login/logout cycle, and each
        // cycle builds a new dashboard.
        _redrawMaps = () => Hub.Post(() => { Cleaning.RebuildScene(); History.RebuildScene(); });
        ThemeService.Changed += _redrawMaps;
        Display.Changed += _redrawMaps;
    }

    public async Task StartAsync()
    {
        try
        {
            if (await ConnectWithRetryAsync() is not { } session) return;
            Hub.Session = session;
            _lastStatus = session.Status;
            session.ConnectionChanged += (s, d) => Hub.Post(() =>
            {
                // The MQTT session comes back on its own after a drop — a wifi blip, or the machine
                // waking from sleep — but the REST side (maps, rooms, history) is only ever fetched
                // on demand, so it would stay as stale as the moment the link died.
                var cameBack = s == RobotConnectionStatus.Connected && _lastStatus != RobotConnectionStatus.Connected && _initialLoadDone;
                _lastStatus = s;
                Hub.Connected = s == RobotConnectionStatus.Connected;
                Hub.Connection = s switch
                {
                    RobotConnectionStatus.Connected => "Connecté",
                    RobotConnectionStatus.Connecting => "Connexion…",
                    RobotConnectionStatus.Reconnecting => "Reconnexion…" + (d is null ? "" : $" ({d})"),
                    _ => "Déconnecté",
                };
                if (cameBack) _ = ReloadAsync("connexion rétablie");
            });
            session.AuthenticationLost += reason => Hub.Post(() => _ = SessionExpiredAsync(reason));
            session.Tracker.StateChanged += st => Hub.Post(() =>
            {
                Status.Apply(st);
                Settings.Apply(st);
                Cleaning.Apply(st);
            });
            session.Tracker.JdmChanged += jdm => Hub.Post(() =>
            {
                Status.ApplyJdm(jdm);
                Schedules.ApplyJdm(jdm);
                Cleaning.ApplyJdm(jdm);
            });
            session.Tracker.CleanPathChanged += path => Hub.Post(() => Cleaning.SetLiveTrail(path));
            session.Tracker.EventReceived += (name, json) => Hub.Post(() =>
            {
                Hub.AddLog($"{name} {Truncate(json.ToJsonString(), 120)}");
                switch (name)
                {
                    case "event.clean_finish.post":
                        NotifyRequested?.Invoke("Nettoyage terminé", "Le robot a terminé son nettoyage.");
                        break;
                    case "event.Unable_all_area_recharge.post":
                        NotifyRequested?.Invoke("Zone inaccessible", "Le robot n'a pas pu atteindre une ou plusieurs pièces sélectionnées.");
                        break;
                }
            });
            // Every message on the robot's topics, regardless of whether the tracker recognises it;
            // only written anywhere while recording is on (see JournalViewModel).
            session.MessageReceived += Journal.CaptureMessage;
            Hub.Connected = true;
            Hub.Connection = "Connecté";

            await Task.WhenAll(Hub.RefreshStateAsync(), Hub.RefreshPropertiesAsync(), Cleaning.LoadMapsAsync(), History.LoadAsync());
            _initialLoadDone = true;
            _refresh.Start();
            _ = History.FillDetailsAsync();
            _ = Schedules.LoadAsync();
        }
        catch (OperationCanceledException) when (Hub.IsShuttingDown) { }
        catch (Exception ex)
        {
            Hub.Message = ex.Message;
            Hub.AddLog(ex.Message);
        }
    }

    /// <summary>
    /// The first connection to the robot, tried again and again until it holds: the network can
    /// drop between the device list and the broker, or the broker be briefly unreachable. Without
    /// this the window would stay "Connexion…" for good, since <see cref="RobotSession"/> only
    /// reconnects a session that once connected. Null when the token is refused (the session-expired
    /// path takes over) or the window is closing.
    /// </summary>
    private async Task<RobotSession?> ConnectWithRetryAsync()
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await _ctx.ConnectAsync(Hub.Ct);
            }
            catch (DysonAuthException ex)
            {
                await SessionExpiredAsync(ex.Message);
                return null;
            }
            catch (Exception ex) when (!Hub.IsShuttingDown)
            {
                var delay = RobotSession.BackoffFor(attempt);
                Hub.Connection = $"Hors ligne, nouvel essai dans {delay.TotalSeconds:F0} s";
                Hub.AddLog($"connexion au robot impossible : {ex.Message}");
                await Task.Delay(delay, Hub.Ct);
            }
        }
    }

    /// <summary>Asks the window to open the map manager; the view models stay clear of windows.</summary>
    public event Action? MapManagerRequested;

    [RelayCommand] private void ManageMaps() => MapManagerRequested?.Invoke();

    /// <summary>Builds the map manager's view model, which shares this session but keeps its own copy of the map.</summary>
    public MapManagerViewModel CreateMapManager() => new(Hub, Display);

    /// <summary>Called once the map manager has changed something, to pick up new names and a new layout.</summary>
    public Task ReloadMapsAsync()
    {
        _maps.Invalidate();
        return Cleaning.LoadMapsAsync();
    }

    [RelayCommand]
    private Task RefreshAsync() => ReloadAsync(null);

    /// <summary>
    /// Fetches everything the robot does not push: state, jdm properties, maps, history. Reached
    /// from the Actualiser button and from a reconnection, which names itself in
    /// <paramref name="reason"/> so the journal says why it happened. Reloads never overlap: a
    /// flapping link would otherwise queue one per transition, and the history detail fill is
    /// several hundred KB per unseen clean.
    /// </summary>
    private async Task ReloadAsync(string? reason)
    {
        if (_reloading) return;
        _reloading = true;
        try
        {
            if (reason is not null) Hub.AddLog($"{reason}, rechargement des données");
            // Drop cached geometry too: furniture, zones or rooms edited from the phone would
            // otherwise keep showing as they were when first loaded.
            _maps.Invalidate();
            await Task.WhenAll(Hub.RefreshStateAsync(), Hub.RefreshPropertiesAsync(), Cleaning.LoadMapsAsync(), History.LoadAsync());
            await Schedules.LoadAsync();
            await History.FillDetailsAsync();
        }
        finally { _reloading = false; }
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";

    // ---- Account ---------------------------------------------------------------

    [RelayCommand]
    private async Task LogoutAsync()
    {
        var result = MessageBox.Show(
            "Vous devrez ressaisir votre e-mail, votre mot de passe et un code reçu par e-mail pour vous reconnecter.\n\nSe déconnecter du compte MyDyson ?",
            "Déconnexion",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes) return;

        await ShutdownAsync();
        SessionStore.Delete();
        LoggedOut?.Invoke();
    }

    /// <summary>The token died mid-session: same exit as a logout, but told rather than asked.</summary>
    private async Task SessionExpiredAsync(string reason)
    {
        Hub.AddLog($"session expirée: {reason}");
        MessageBox.Show(
            "La session MyDyson n'est plus acceptée par le cloud Dyson. Vous devez vous reconnecter.",
            "Session expirée", MessageBoxButton.OK, MessageBoxImage.Warning);
        await ShutdownAsync();
        SessionStore.Delete();
        LoggedOut?.Invoke();
    }

    public async Task ShutdownAsync()
    {
        _refresh.Stop();
        Hub.CancelLifetime();
        Journal.Dispose();
        await _ctx.DisposeAsync();
    }

    /// <summary>Releases what the dashboard owns outright; the robot context is released by <see cref="ShutdownAsync"/>.</summary>
    public void Dispose()
    {
        _refresh.Stop();
        ThemeService.Changed -= _redrawMaps;
        Display.Changed -= _redrawMaps;
        Journal.Dispose();
        Cleaning.Dispose();
        Hub.Dispose();
    }
}
