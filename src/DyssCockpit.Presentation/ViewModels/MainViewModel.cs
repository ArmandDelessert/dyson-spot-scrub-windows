using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DyssCockpit.Presentation.Services;
using DyssCockpit.Core;

using static DyssCockpit.Core.Translation;

namespace DyssCockpit.Presentation.ViewModels;

/// <summary>
/// The dashboard: connects to the robot, routes what it pushes to the tab view models, and owns
/// the session's lifetime (refresh timer, logout, expiry, shutdown). The tabs themselves live in
/// <see cref="StatusViewModel"/>, <see cref="CleaningViewModel"/>, <see cref="HistoryViewModel"/>,
/// <see cref="SchedulesViewModel"/>, <see cref="RobotSettingsViewModel"/> and
/// <see cref="JournalViewModel"/>, sharing a <see cref="RobotHub"/>.
/// </summary>
public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    /// <summary>How often the state is asked for, on top of what the robot pushes.</summary>
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(30);

    private readonly RobotContext _ctx;
    private readonly Action _redrawMaps;
    private bool _initialLoadDone;
    private bool _reloading;
    private RobotConnectionStatus _lastStatus = RobotConnectionStatus.Disconnected;
    private readonly MapCatalog _maps;
    private readonly CleaningTaskDetector _tasks = new();

    /// <summary>How long after this window sent the robot to clean its rooms are still the ones a start is told with.</summary>
    private static readonly TimeSpan LaunchWindow = TimeSpan.FromMinutes(2);

    public RobotHub Hub { get; }
    public StatusViewModel Status { get; }
    public CleaningViewModel Cleaning { get; }
    public HistoryViewModel History { get; }
    public RobotSettingsViewModel RobotSettings { get; }
    public JournalViewModel Journal { get; }
    public SchedulesViewModel Schedules { get; }
    /// <summary>The application's own settings; unlike the robot settings, none of it is sent to the robot.</summary>
    public AppSettings AppSettings { get; }

    public string RobotName => _ctx.Robot?.Name ?? "Robot";
    public string Serial => _ctx.Robot?.SerialNumber ?? "";
    public string Firmware => _ctx.Robot?.ConnectedConfiguration?.Firmware?.Version ?? "";
    /// <summary>The only account information the login flow ever returns: no display name, just the email used to sign in.</summary>
    public string AccountEmail => _ctx.Stored.Email;

    /// <summary>
    /// The robot in one line, for the tooltip of the notification area's icon: its name, then what
    /// it is doing and its battery, a fault if it has one, or why there is nothing to say.
    /// </summary>
    public string Summary =>
        !Hub.Connected ? T($"{RobotName} : {Hub.Connection}", $"{RobotName}: {Hub.Connection}")
        : string.IsNullOrEmpty(Status.StateText) ? RobotName
        : T($"{RobotName} : ", $"{RobotName}: ") + $"{Status.StateText} · {Status.Battery} %" + (Status.HasRealFault ? $" · {Status.FaultText}" : "");

    /// <summary>A fault the user should see: the icon in the notification area then carries a badge.</summary>
    public bool NeedsAttention => Status.HasRealFault;

    /// <summary>Raised when <see cref="Summary"/> or <see cref="NeedsAttention"/> may have changed.</summary>
    public event Action? SummaryChanged;

    public event Action? LoggedOut;
    /// <summary>A robot event worth a Windows notification: title, body.</summary>
    public event Action<string, string>? NotifyRequested;

    /// <param name="ui">The UI thread, where everything the robot pushes is applied.</param>
    /// <param name="dialogs">The questions the dashboard and the windows it opens put to the user.</param>
    /// <param name="time">The clock of the refresh timer and of every wait; the system's unless a test moves it by hand.</param>
    /// <param name="settings">The display preferences; null reads the stored ones.</param>
    public MainViewModel(RobotContext ctx, IUiDispatcher ui, IDialogService dialogs, TimeProvider? time = null, AppSettings? settings = null)
    {
        _ctx = ctx;
        Hub = new RobotHub(ctx, ui, dialogs, time);
        AppSettings = settings ?? AppSettings.Load();
        _maps = new MapCatalog(Hub);
        Status = new StatusViewModel(Hub);
        Cleaning = new CleaningViewModel(Hub, _maps, AppSettings);
        History = new HistoryViewModel(Hub, _maps, AppSettings);
        RobotSettings = new RobotSettingsViewModel(Hub);
        Journal = new JournalViewModel(Hub, AppSettings);
        Schedules = new SchedulesViewModel(Hub, _maps, () => History.History.Select(c => c.Summary));

        // Both maps bake in the display preferences, so a change has to redraw them rather than
        // wait for the next robot message. (A change of theme is the map views' own business: they
        // redraw themselves in the new colours.)
        _redrawMaps = () => Hub.Post(() => { Cleaning.RebuildScene(); History.RebuildScene(); });
        AppSettings.Changed += _redrawMaps;

        Status.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(StatusViewModel.StateText) or nameof(StatusViewModel.Battery)
                or nameof(StatusViewModel.HasRealFault) or nameof(StatusViewModel.FaultText))
                SummaryChanged?.Invoke();
        };
        Hub.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(RobotHub.Connected) or nameof(RobotHub.Connection)) SummaryChanged?.Invoke();
        };
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
                // What the robot does while the link is down is not seen starting: the first state
                // after it is a starting point, not a clean that has just begun.
                if (s != RobotConnectionStatus.Connected) _tasks.Reset();
                Hub.Connected = s == RobotConnectionStatus.Connected;
                Hub.Connection = s switch
                {
                    RobotConnectionStatus.Connected => T("Connecté", "Connected"),
                    RobotConnectionStatus.Connecting => T("Connexion…", "Connecting…"),
                    RobotConnectionStatus.Reconnecting => T("Reconnexion…", "Reconnecting…") + (d is null ? "" : $" ({d})"),
                    _ => T("Déconnecté", "Disconnected"),
                };
                if (cameBack) _ = ReloadAsync(T("connexion rétablie", "connection restored"));
            });
            session.AuthenticationLost += reason => Hub.Post(() => _ = SessionExpiredAsync(reason));
            session.Tracker.StateChanged += st => Hub.Post(() => OnRobotState(st));
            session.Tracker.JdmChanged += jdm => Hub.Post(() =>
            {
                Status.ApplyJdm(jdm);
                Schedules.ApplyJdm(jdm);
                Cleaning.ApplyJdm(jdm);
            });
            session.Tracker.CleanPathChanged += path => Hub.Post(() => Cleaning.SetLiveTrail(path));
            session.Tracker.EventReceived += (name, json) => Hub.Post(() => OnRobotEvent(name, json));
            // Every message on the robot's topics, regardless of whether the tracker recognises it;
            // only written anywhere while recording is on (see JournalViewModel).
            session.MessageReceived += Journal.CaptureMessage;
            Hub.Connected = true;
            Hub.Connection = T("Connecté", "Connected");

            await Task.WhenAll(Hub.RefreshStateAsync(), Hub.RefreshPropertiesAsync(), Cleaning.LoadMapsAsync(), History.LoadAsync());
            _initialLoadDone = true;
            _ = RefreshPeriodicallyAsync();
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
    /// The robot's state, a new one: shown on every tab, and, when it starts a clean, told as a
    /// Windows notification if the settings ask for it. On the UI thread.
    /// </summary>
    public void OnRobotState(RobotState state)
    {
        Status.Apply(state);
        RobotSettings.Apply(state);
        Cleaning.Apply(state);
        NotifyTask(_tasks.OnState(state));
    }

    /// <summary>
    /// An event the robot pushed: written to the log, and turned into a Windows notification when the
    /// settings ask for one. On the UI thread.
    /// </summary>
    public void OnRobotEvent(string name, JsonObject json)
    {
        Hub.AddLog($"{name} {Truncate(json.ToJsonString(), 120)}");
        NotifyTask(_tasks.OnEvent(name, json));
        switch (name)
        {
            // The robot's report of the clean, finished or not: the cloud files it as a history
            // entry, which is also what the phone's end-of-clean notification follows.
            case "event.clean_record.post":
                _ = History.RefreshAfterCleanAsync();
                break;
            case "event.Unable_all_area_recharge.post":
                if (AppSettings.NotifyUnreachable)
                    NotifyRequested?.Invoke(T("Zone inaccessible", "Unreachable area"), T("Le robot n'a pas pu atteindre une ou plusieurs pièces sélectionnées.", "The robot could not reach one or more of the selected rooms."));
                break;
        }
    }

    /// <summary>
    /// What a clean starting, ending or being given up becomes: a notification, or nothing,
    /// according to <see cref="AppSettings.TaskNotifications"/>. A clean given up counts as an end,
    /// since it is the moment the user would otherwise wait for in vain.
    /// </summary>
    private void NotifyTask(CleaningTaskNotice? notice)
    {
        var mode = AppSettings.TaskNotifications;
        switch (notice)
        {
            case { Change: CleaningTaskChange.Started } when mode == TaskNotificationMode.StartAndEnd:
                NotifyRequested?.Invoke(T("Nettoyage commencé", "Cleaning started"), StartedText(notice));
                break;
            case { Change: CleaningTaskChange.Finished } when mode != TaskNotificationMode.None:
                NotifyRequested?.Invoke(T("Nettoyage terminé", "Cleaning finished"), FinishedText(notice.Minutes));
                break;
            case { Change: CleaningTaskChange.Abandoned } when mode != TaskNotificationMode.None:
                NotifyRequested?.Invoke(T("Nettoyage interrompu", "Cleaning interrupted"), T("Le robot a abandonné son nettoyage.", "The robot gave up cleaning."));
                break;
        }
    }

    /// <summary>
    /// The robot's messages never say which rooms it set off for, so they are named only when this
    /// window has just sent it to them; a clean begun from the phone, a schedule or the robot's own
    /// button is told as it is, a zone being the one thing its state does say.
    /// </summary>
    private string StartedText(CleaningTaskNotice notice)
    {
        if (Cleaning.LastLaunch is { } launch && Hub.Time.GetUtcNow() - launch.At <= LaunchWindow)
        {
            var rooms = launch.Rooms;
            if (rooms.Count == 0) return T("Le robot nettoie la zone.", "The robot is cleaning the zone.");
            var names = rooms.Count <= 3
                ? string.Join(", ", rooms)
                : string.Join(", ", rooms.Take(3)) + T($" et {rooms.Count - 3} autre(s)", $" and {rooms.Count - 3} more");
            return T($"Le robot nettoie : {names}.", $"The robot is cleaning: {names}.");
        }
        return notice.IsZone
            ? T("Le robot nettoie une zone.", "The robot is cleaning a zone.")
            : T("Le robot a commencé son nettoyage.", "The robot has started cleaning.");
    }

    /// <summary>How long it took, when the robot's report gives one (<c>record_use_time</c>, in minutes).</summary>
    private static string FinishedText(int? minutes)
    {
        if (minutes is not > 0) return T("Le robot a terminé son nettoyage.", "The robot has finished cleaning.");
        var duration = ScheduleEditorViewModel.FormatMinutes(minutes.Value);
        return T($"Le robot a terminé son nettoyage en {duration}.", $"The robot has finished cleaning in {duration}.");
    }

    /// <summary>
    /// Asks for the state every <see cref="RefreshInterval"/> until the dashboard closes. Started
    /// from the UI thread, so each refresh lands there too.
    /// </summary>
    private async Task RefreshPeriodicallyAsync()
    {
        using var timer = new PeriodicTimer(RefreshInterval, Hub.Time);
        try
        {
            while (await timer.WaitForNextTickAsync(Hub.Ct)) await Hub.RefreshStateAsync();
        }
        catch (OperationCanceledException) { }   // shutting down
    }

    /// <summary>
    /// The first connection to the robot, tried again and again until it holds: the network can
    /// drop between the device list and the broker, or the broker be briefly unreachable. Without
    /// this the window would stay T("Connexion…", "Connecting…") for good, since <see cref="RobotSession"/> only
    /// reconnects a session that once connected. Null when the token is refused (the session-expired
    /// path takes over) or the window is closing.
    /// </summary>
    private async Task<RobotSession?> ConnectWithRetryAsync()
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await _ctx.ConnectAsync(Hub.Time, Hub.Ct);
            }
            catch (DysonAuthException ex)
            {
                await SessionExpiredAsync(ex.Message);
                return null;
            }
            catch (Exception ex) when (!Hub.IsShuttingDown)
            {
                var delay = RobotSession.BackoffFor(attempt);
                Hub.Connection = T($"Hors ligne, nouvel essai dans {delay.TotalSeconds:F0} s", $"Offline, retrying in {delay.TotalSeconds:F0} s");
                Hub.AddLog(T($"connexion au robot impossible : {ex.Message}", $"cannot connect to the robot: {ex.Message}"));
                await Task.Delay(delay, Hub.Time, Hub.Ct);
            }
        }
    }

    /// <summary>Asks the window to open the map manager; the view models stay clear of windows.</summary>
    public event Action? MapManagerRequested;

    [RelayCommand] private void ManageMaps() => MapManagerRequested?.Invoke();

    /// <summary>Builds the map manager's view model, which shares this session but keeps its own copy of the map.</summary>
    public MapManagerViewModel CreateMapManager() => new(Hub, AppSettings);

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
            if (reason is not null) Hub.AddLog(T($"{reason}, rechargement des données", $"{reason}, reloading the data"));
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
        if (!await Hub.Dialogs.ConfirmAsync(T("Déconnexion", "Log out"),
                T("Vous devrez ressaisir votre e-mail, votre mot de passe et un code reçu par e-mail pour vous reconnecter.\n\nSe déconnecter du compte MyDyson ?", "You will have to enter your e-mail, your password and a code sent by e-mail to log in again.\n\nLog out of the MyDyson account?")))
            return;

        await ShutdownAsync();
        SessionStore.Delete();
        LoggedOut?.Invoke();
    }

    /// <summary>The token died mid-session: same exit as a logout, but told rather than asked.</summary>
    private async Task SessionExpiredAsync(string reason)
    {
        Hub.AddLog(T($"session expirée: {reason}", $"session expired: {reason}"));
        await Hub.Dialogs.AlertAsync(T("Session expirée", "Session expired"),
            T("La session MyDyson n'est plus acceptée par le cloud Dyson. Vous devez vous reconnecter.", "The Dyson cloud no longer accepts the MyDyson session. Please log in again."));
        await ShutdownAsync();
        SessionStore.Delete();
        LoggedOut?.Invoke();
    }

    /// <summary>Stops the periodic refresh and every REST call in flight, then closes the robot session.</summary>
    public async Task ShutdownAsync()
    {
        Hub.CancelLifetime();
        Journal.Dispose();
        await _ctx.DisposeAsync();
    }

    /// <summary>Releases what the dashboard owns outright; the robot context is released by <see cref="ShutdownAsync"/>.</summary>
    public void Dispose()
    {
        AppSettings.Changed -= _redrawMaps;
        Journal.Dispose();
        Cleaning.Dispose();
        Hub.Dispose();
    }
}
