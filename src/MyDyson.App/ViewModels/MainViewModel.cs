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
/// <see cref="SettingsViewModel"/> and <see cref="JournalViewModel"/>, sharing a
/// <see cref="RobotHub"/>.
/// </summary>
public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly RobotContext _ctx;
    private readonly DispatcherTimer _refresh;
    private readonly Action _onThemeChanged;

    public RobotHub Hub { get; }
    public StatusViewModel Status { get; }
    public CleaningViewModel Cleaning { get; }
    public HistoryViewModel History { get; }
    public SettingsViewModel Settings { get; }
    public JournalViewModel Journal { get; }

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
        var maps = new MapCatalog(Hub);
        Status = new StatusViewModel(Hub);
        Cleaning = new CleaningViewModel(Hub, maps);
        History = new HistoryViewModel(Hub, maps);
        Settings = new SettingsViewModel(Hub);
        Journal = new JournalViewModel(Hub);

        _refresh = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _refresh.Tick += async (_, _) => await Hub.RefreshStateAsync();
        // Kept in a field so Dispose can unsubscribe: ThemeService is static and outlives every
        // login/logout cycle, and each cycle builds a new dashboard.
        _onThemeChanged = () => Hub.Post(() => { Cleaning.RebuildScene(); History.RebuildScene(); });
        ThemeService.Changed += _onThemeChanged;
    }

    public async Task StartAsync()
    {
        try
        {
            var session = await _ctx.ConnectAsync(Hub.Ct);
            Hub.Session = session;
            session.ConnectionChanged += (s, d) => Hub.Post(() =>
            {
                Hub.Connected = s == RobotConnectionStatus.Connected;
                Hub.Connection = s switch
                {
                    RobotConnectionStatus.Connected => "Connecté",
                    RobotConnectionStatus.Connecting => "Connexion…",
                    RobotConnectionStatus.Reconnecting => "Reconnexion…" + (d is null ? "" : $" ({d})"),
                    _ => "Déconnecté",
                };
            });
            session.AuthenticationLost += reason => Hub.Post(() => _ = SessionExpiredAsync(reason));
            session.Tracker.StateChanged += st => Hub.Post(() =>
            {
                Status.Apply(st);
                Settings.Apply(st);
                Cleaning.Apply(st);
            });
            session.Tracker.JdmChanged += jdm => Hub.Post(() => Status.ApplyJdm(jdm));
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
            // only written anywhere once a capture file has been opened (see JournalViewModel).
            session.MessageReceived += Journal.CaptureMessage;
            Hub.Connected = true;
            Hub.Connection = "Connecté";

            await Task.WhenAll(Hub.RefreshStateAsync(), Hub.RefreshPropertiesAsync(), Cleaning.LoadMapsAsync(), History.LoadAsync());
            _refresh.Start();
            _ = History.FillDetailsAsync();
        }
        catch (OperationCanceledException) when (Hub.IsShuttingDown) { }
        catch (Exception ex)
        {
            Hub.Message = ex.Message;
            Hub.AddLog(ex.Message);
        }
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        await Task.WhenAll(Hub.RefreshStateAsync(), Cleaning.LoadMapsAsync(), History.LoadAsync());
        _ = History.FillDetailsAsync();
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
        ThemeService.Changed -= _onThemeChanged;
        Journal.Dispose();
        Cleaning.Dispose();
        Hub.Dispose();
    }
}
