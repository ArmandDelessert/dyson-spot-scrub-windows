using System.Text.Json.Nodes;

namespace MyDyson.Core;

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
        client.Disconnected += reason => _ = OnDisconnectedAsync(client, reason);

        await client.ConnectAsync(ct).ConfigureAwait(false);
        _client = client;
        _attempt = 0;
        SetStatus(RobotConnectionStatus.Connected, null);
    }

    private void OnMessage(RobotMessage message)
    {
        MessageReceived?.Invoke(message);
        Tracker.Apply(message);
    }

    private async Task OnDisconnectedAsync(RobotMqttClient source, string reason)
    {
        // Ignore drops of a client we already replaced, and stop once disposed.
        if (!ReferenceEquals(source, _client) || _lifetime.IsCancellationRequested) return;

        SetStatus(RobotConnectionStatus.Reconnecting, reason);
        while (!_lifetime.IsCancellationRequested)
        {
            _attempt++;
            var delay = TimeSpan.FromSeconds(Math.Min(120, 5 * Math.Pow(2, _attempt - 1)));
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
                SetStatus(RobotConnectionStatus.Disconnected, "session expirée");
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
