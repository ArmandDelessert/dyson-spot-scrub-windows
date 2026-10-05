using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Dyss.Presentation.Services;
using Dyss.Core;
using Microsoft.Extensions.Logging;

using static Dyss.Core.Translation;

namespace Dyss.Presentation.ViewModels;

/// <summary>
/// What every part of the dashboard shares: the cloud context and, once connected, the robot
/// session; the UI dispatcher and the dialogs, both provided by the UI; the journal; the connection
/// and busy strip; the lifetime token that stops REST calls on shutdown; and <see cref="RunAsync"/>,
/// the one way commands reach the robot. The tab view models (<see cref="StatusViewModel"/>, <see cref="CleaningViewModel"/>,
/// <see cref="HistoryViewModel"/>, <see cref="SettingsViewModel"/>, <see cref="JournalViewModel"/>)
/// each take one of these; <see cref="MainViewModel"/> owns it and wires the session into it.
/// </summary>
public sealed partial class RobotHub : ObservableObject, IDisposable
{
    private readonly RobotContext _ctx;
    private readonly IUiDispatcher _ui;
    /// <summary>Cancelled by <see cref="MainViewModel.ShutdownAsync"/> so REST calls still in flight stop instead of landing on view models that are going away.</summary>
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ILogger _logger;

    /// <param name="time">The clock every wait of the dashboard runs on; tests pass one they move by hand.</param>
    public RobotHub(RobotContext ctx, IUiDispatcher ui, IDialogService dialogs, TimeProvider? time = null)
    {
        _ctx = ctx;
        _ui = ui;
        Dialogs = dialogs;
        Time = time ?? TimeProvider.System;
        _logger = ctx.Loggers.CreateLogger<RobotHub>();
        // Already in the application's log, written there by the session's own logger.
        _ctx.Log += m => Post(() => ShowInJournal(m));
    }

    /// <summary>Questions for the user: confirmations, names, the schedule editor, where to save a file.</summary>
    public IDialogService Dialogs { get; }

    /// <summary>The clock behind the refresh timer, the reconnection waits and the pause after a command.</summary>
    public TimeProvider Time { get; }

    public DysonCloudClient Api => _ctx.Api;
    public string Serial => _ctx.Robot?.SerialNumber ?? "";
    /// <summary>The culture chosen at login ("fr-CH"): the language the robot names new rooms in.</summary>
    public string Culture => string.IsNullOrWhiteSpace(_ctx.Stored.Culture) ? "fr-CH" : _ctx.Stored.Culture;
    /// <summary>The manifest's device type, "804" for the RB05: what the cloud scheduler files its schedules under.</summary>
    public string ProductType => string.IsNullOrWhiteSpace(_ctx.Robot?.Type) ? "804" : _ctx.Robot.Type;
    /// <summary>Null until <see cref="MainViewModel.StartAsync"/> has connected.</summary>
    public RobotSession? Session { get; internal set; }
    public CancellationToken Ct => _lifetime.Token;
    public bool IsShuttingDown => _lifetime.IsCancellationRequested;

    public ObservableCollection<string> Log { get; } = new();

    [ObservableProperty] private string _connection = T("Connexion…", "Connecting…");
    [ObservableProperty] private bool _connected;
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private bool _busy;

    /// <summary>Runs on the UI thread, now if already there. Everything the robot pushes arrives on the MQTT thread and goes through here.</summary>
    public void Post(Action a)
    {
        if (_ui.CheckAccess()) a(); else _ui.Post(a);
    }

    /// <summary>A line for the Journal tab, kept in the application's log too.</summary>
    public void AddLog(string line)
    {
        LogJournalLine(_logger, line);
        ShowInJournal(line);
    }

    private void ShowInJournal(string line)
    {
        Log.Insert(0, $"{DateTime.Now:HH:mm:ss} {line}");
        while (Log.Count > 200) Log.RemoveAt(Log.Count - 1);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "{Line}")]
    private static partial void LogJournalLine(ILogger logger, string line);

    /// <summary>Sends a command, journals it, then asks for a fresh state a moment later so the dashboard reflects the result.</summary>
    public async Task RunAsync(string label, Func<RobotMqttClient, Task> action)
    {
        if (Session?.Client is not { IsConnected: true } client) { Message = T("Robot non connecté.", "Robot not connected."); return; }
        Busy = true;
        Message = "";
        try
        {
            await action(client);
            AddLog(label);
            await Task.Delay(TimeSpan.FromMilliseconds(1500), Time, Ct);
            await RefreshStateAsync();
        }
        catch (OperationCanceledException) when (IsShuttingDown) { }
        catch (Exception ex)
        {
            Message = ex.Message;
            AddLog($"{label}: {ex.Message}");
        }
        finally { Busy = false; }
    }

    public async Task RefreshStateAsync()
    {
        if (Session is null || Session.Status != RobotConnectionStatus.Connected) return;
        // Runs from an async-void timer tick: anything escaping here is an unhandled exception that
        // ends the process, and a periodic refresh is never worth that, whatever went wrong.
        try { await Session.RefreshStateAsync(Ct); }
        catch (OperationCanceledException) when (IsShuttingDown) { }
        catch (Exception ex) { AddLog(T($"état: {ex.Message}", $"state: {ex.Message}")); }
    }

    /// <summary>One prop.get at start-up, so jdm-only facts (drying countdown, trip back to wash) show at once instead of at their next push.</summary>
    public async Task RefreshPropertiesAsync()
    {
        if (Session is null || Session.Status != RobotConnectionStatus.Connected) return;
        try { await Session.RefreshPropertiesAsync(Ct); }
        catch (OperationCanceledException) when (IsShuttingDown) { }
        catch (Exception ex) { AddLog(T($"propriétés: {ex.Message}", $"properties: {ex.Message}")); }
    }

    /// <summary>Stops every REST call and timer-driven refresh; the session itself is closed by the context's disposal.</summary>
    internal void CancelLifetime() => _lifetime.Cancel();

    public void Dispose()
    {
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}
