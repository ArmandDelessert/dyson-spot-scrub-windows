using System.Text.Json.Nodes;

using static Dyss.Core.Translation;

namespace Dyss.Core;

public enum RobotConnectionStatus { Disconnected, Connecting, Connected, Reconnecting }

/// <summary>
/// Long-lived connection to one robot: fetches broker credentials, connects, keeps a
/// <see cref="RobotStateTracker"/> current, and reconnects with fresh credentials after any drop.
/// Reconnection uses exponential backoff from 5 s to 2 min. Credentials are re-fetched on every
/// attempt, since the custom-authorizer token is short-lived.
/// </summary>
public sealed class RobotSession : IAsyncDisposable
{
    private readonly DysonCloudClient _api;
    private readonly Device _device;
    private readonly Action<string>? _log;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _connectLock = new(1, 1);

    private RobotMqttClient? _client;
    private int _attempt;

    public string Serial => _device.SerialNumber;
    public string TopicPrefix { get; private set; }
    public RobotStateTracker Tracker { get; } = new();
    public RobotConnectionStatus Status { get; private set; } = RobotConnectionStatus.Disconnected;
    public RobotMqttClient? Client => _client;

    public event Action<RobotConnectionStatus, string?>? ConnectionChanged;
    public event Action<RobotMessage>? MessageReceived;
    /// <summary>
    /// The bearer token stopped being accepted while reconnecting: no amount of retrying will get
    /// the robot back, only a fresh login. Retrying would otherwise show "Reconnexion…" forever.
    /// </summary>
    public event Action<string>? AuthenticationLost;

    public RobotSession(DysonCloudClient api, Device device, Action<string>? log = null)
    {
        _api = api;
        _device = device;
        _log = log;
        TopicPrefix = device.GuessTopicPrefix();
    }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        await _connectLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await ConnectOnceAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _connectLock.Release();
        }
    }

    private async Task ConnectOnceAsync(CancellationToken ct)
    {
        SetStatus(_attempt == 0 ? RobotConnectionStatus.Connecting : RobotConnectionStatus.Reconnecting, null);

        var old = _client;
        _client = null;
        if (old is not null) await old.DisposeAsync().ConfigureAwait(false);

        var iot = await _api.GetIotCredentialsAsync(Serial, ct).ConfigureAwait(false);
        var client = new RobotMqttClient(Serial, TopicPrefix, MqttEndpoint.FromCustomAuthorizerTls(iot));
        client.MessageReceived += OnMessage;
        client.PrefixChanged += p => TopicPrefix = p;
        client.ListenerFailed += ex => _log?.Invoke(T($"message non traité : {ex.Message}", $"message not handled: {ex.Message}"));
        client.Disconnected += reason => _ = OnDisconnectedAsync(client, reason);

        try
        {
            await client.ConnectAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            // A client that never connected still holds its socket and timers.
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        _client = client;
        _attempt = 0;
        SetStatus(RobotConnectionStatus.Connected, null);
    }

    /// <summary>
    /// Runs on the MQTT client's receive loop. One malformed or unexpected message — a field of
    /// another type than the captures showed, a capture file that can no longer be written — must
    /// cost that message only, not the loop that brings in every later one.
    /// </summary>
    private void OnMessage(RobotMessage message)
    {
        try { MessageReceived?.Invoke(message); }
        catch (Exception ex) { _log?.Invoke(T($"message {message.Topic} : {ex.Message}", $"message {message.Topic}: {ex.Message}")); }
        try { Tracker.Apply(message); }
        catch (Exception ex) { _log?.Invoke(T($"message {message.Topic} ignoré : {ex.Message}", $"message {message.Topic} ignored: {ex.Message}")); }
    }

    private async Task OnDisconnectedAsync(RobotMqttClient source, string reason)
    {
        // Ignore drops of a client we already replaced, and stop once disposed.
        if (!ReferenceEquals(source, _client) || _lifetime.IsCancellationRequested) return;

        SetStatus(RobotConnectionStatus.Reconnecting, reason);
        while (!_lifetime.IsCancellationRequested)
        {
            _attempt++;
            var delay = BackoffFor(_attempt);
            _log?.Invoke($"MQTT dropped ({reason}); reconnecting in {delay.TotalSeconds:F0} s (attempt {_attempt})");
            try
            {
                await Task.Delay(delay, _lifetime.Token).ConfigureAwait(false);
                await _connectLock.WaitAsync(_lifetime.Token).ConfigureAwait(false);
                try
                {
                    await ConnectOnceAsync(_lifetime.Token).ConfigureAwait(false);
                    return;
                }
                finally
                {
                    _connectLock.Release();
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (DysonAuthException ex)
            {
                _log?.Invoke($"reconnect refused, token no longer valid: {ex.Message}");
                SetStatus(RobotConnectionStatus.Disconnected, T("session expirée", "session expired"));
                AuthenticationLost?.Invoke(ex.Message);
                return;
            }
            catch (Exception ex)
            {
                reason = ex.Message;
                _log?.Invoke($"reconnect failed: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// How long to wait before reconnection attempt <paramref name="attempt"/> (1-based): 5 s
    /// doubling each time, capped at 2 minutes. The cap matters more than the growth — a robot can
    /// be off the network for hours, and an uncapped backoff would leave the app hours behind it.
    /// </summary>
    public static TimeSpan BackoffFor(int attempt) =>
        TimeSpan.FromSeconds(Math.Min(120, 5 * Math.Pow(2, Math.Max(1, attempt) - 1)));

    private void SetStatus(RobotConnectionStatus status, string? detail)
    {
        Status = status;
        ConnectionChanged?.Invoke(status, detail);
    }

    private RobotMqttClient Connected =>
        _client is { IsConnected: true } c ? c : throw new InvalidOperationException("Not connected to the robot.");

    // ---- Convenience wrappers over the current client -------------------------

    public Task<RobotState> RefreshStateAsync(CancellationToken ct = default) =>
        Connected.RequestStateAsync(ct: ct);

    public Task<JsonObject> RequestJdmAsync(string method, JsonObject? parameters = null, CancellationToken ct = default) =>
        Connected.RequestJdmAsync(method, parameters, ct: ct);

    /// <summary>
    /// Requests the full jdm property set, which the tracker merges when the reply arrives. On top
    /// of the official app's list it asks for the two properties the robot only ever pushes to the
    /// app (verified answered by prop.get on 2026-09-22), so their state is known at start-up
    /// rather than at their next push.
    /// </summary>
    public Task<JsonObject> RefreshPropertiesAsync(CancellationToken ct = default)
    {
        var names = new JsonArray();
        foreach (var n in RobotMqttClient.AppPropertyNames) names.Add(n);
        foreach (var n in PushOnlyPropertyNames) names.Add(n);
        return Connected.RequestJdmAsync("prop.get", new JsonObject { ["property"] = names }, ct: ct);
    }

    /// <summary>Properties the app never requests but the robot pushes and answers for: see <see cref="JdmProperties.WorkTime"/> and <see cref="JdmProperties.BackToWash"/>.</summary>
    public static readonly string[] PushOnlyPropertyNames = ["work_time", "back_to_wash"];

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        var c = _client;
        _client = null;
        if (c is not null) await c.DisposeAsync().ConfigureAwait(false);
        SetStatus(RobotConnectionStatus.Disconnected, "disposed");
        _lifetime.Dispose();
        _connectLock.Dispose();
    }
}
