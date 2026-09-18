using System.Text.Json;
using System.Text.Json.Nodes;
using MQTTnet;
using MQTTnet.Formatter;
using MQTTnet.Protocol;

namespace MyDyson.Core;

public sealed record RobotMessage(DateTimeOffset ReceivedUtc, string Topic, string Payload)
{
    public JsonNode? Json
    {
        get { try { return JsonNode.Parse(Payload); } catch (JsonException) { return null; } }
    }

    /// <summary>"msg" for classic Dyson messages, "method" for the JDM (JSON-RPC-like) layer.</summary>
    public string? Kind => Json?["msg"]?.GetValue<string>() ?? Json?["method"]?.GetValue<string>();
}

/// <summary>
/// MQTT-over-WebSocket client to the Dyson AWS IoT broker for one robot.
///
/// Topics (prefix = "RB05" for the Spot+Scrub AI):
///   {prefix}/{serial}/status           robot -> app (CURRENT-STATE, STATE-CHANGE, position updates, faults)
///   {prefix}/{serial}/status/jdm       robot -> app, JDM layer (prop.post, service.* replies)
///   {prefix}/{serial}/command          app -> robot, classic Dyson JSON ({"msg": "...", ...})
///   {prefix}/{serial}/command/jdm      app -> robot, JDM layer ({"method": "service.x", "params": {...}})
///
/// The client subscribes to +/{serial}/# and adopts whatever prefix the robot really publishes on.
/// </summary>
public sealed class RobotMqttClient : IAsyncDisposable
{
    private readonly IMqttClient _client;
    private readonly IotData _iot;
    private string _prefix;

    public string Serial { get; }
    public string Prefix => _prefix;
    public bool IsConnected => _client.IsConnected;

    public event Action<RobotMessage>? MessageReceived;
    public event Action<string>? Disconnected;
    public event Action<string>? PrefixChanged;

    public RobotMqttClient(string serial, string topicPrefix, IotData iotCredentials)
    {
        Serial = serial;
        _prefix = topicPrefix;
        _iot = iotCredentials;
        _client = new MqttClientFactory().CreateMqttClient();
        _client.ApplicationMessageReceivedAsync += OnMessageAsync;
        _client.DisconnectedAsync += e =>
        {
            Disconnected?.Invoke(e.ReasonString ?? e.Reason.ToString());
            return Task.CompletedTask;
        };
    }

    public string CommandTopic => $"{_prefix}/{Serial}/command";
    public string JdmCommandTopic => $"{_prefix}/{Serial}/command/jdm";

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        var options = new MqttClientOptionsBuilder()
            .WithClientId(_iot.IoTCredentials.ClientId)
            .WithWebSocketServer(o => o.WithUri(_iot.BuildWebSocketUri()))
            .WithTlsOptions(o => o.UseTls())
            .WithProtocolVersion(MqttProtocolVersion.V311)
            .WithKeepAlivePeriod(TimeSpan.FromSeconds(30))
            .WithCleanSession()
            .Build();

        var result = await _client.ConnectAsync(options, ct).ConfigureAwait(false);
        if (result.ResultCode != MqttClientConnectResultCode.Success)
            throw new DysonApiException($"MQTT connect failed: {result.ResultCode} {result.ReasonString}");

        await _client.SubscribeAsync($"+/{Serial}/#", MqttQualityOfServiceLevel.AtMostOnce, ct).ConfigureAwait(false);
    }

    private Task OnMessageAsync(MqttApplicationMessageReceivedEventArgs e)
    {
        var topic = e.ApplicationMessage.Topic;
        var payload = e.ApplicationMessage.ConvertPayloadToString() ?? "";

        // Adopt the prefix the robot actually uses (manifest may say something else).
        var parts = topic.Split('/');
        if (parts.Length >= 2 && parts[1] == Serial && parts[0] != _prefix)
        {
            _prefix = parts[0];
            PrefixChanged?.Invoke(_prefix);
        }

        MessageReceived?.Invoke(new RobotMessage(DateTimeOffset.UtcNow, topic, payload));
        return Task.CompletedTask;
    }

    private static string NowIso() => DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");

    /// <summary>Publishes a raw JSON string on an arbitrary topic.</summary>
    public Task PublishRawAsync(string topic, string json, CancellationToken ct = default) =>
        _client.PublishStringAsync(topic, json, MqttQualityOfServiceLevel.AtMostOnce, cancellationToken: ct);

    /// <summary>Publishes a classic Dyson command ({"msg": ..., "time": ..., ...}).</summary>
    public Task PublishCommandAsync(JsonObject payload, CancellationToken ct = default)
    {
        payload["time"] ??= NowIso();
        return PublishRawAsync(CommandTopic, payload.ToJsonString(), ct);
    }

    /// <summary>Publishes a JDM-layer request ({"method": "service.x", "params": {...}}).</summary>
    public Task PublishJdmAsync(string method, JsonObject? parameters = null, CancellationToken ct = default)
    {
        var payload = new JsonObject
        {
            ["msgId"] = Random.Shared.NextInt64(100_000_000_000L, 999_999_999_999L).ToString(),
            ["version"] = "1.0.1",
            ["method"] = method,
            ["params"] = parameters ?? new JsonObject(),
            ["time"] = NowIso(),
        };
        return PublishRawAsync(JdmCommandTopic, payload.ToJsonString(), ct);
    }

    // ---- Well-known commands -------------------------------------------------

    public Task RequestCurrentStateAsync(CancellationToken ct = default) =>
        PublishCommandAsync(new JsonObject { ["msg"] = "REQUEST-CURRENT-STATE" }, ct);

    public Task RequestCurrentFaultsAsync(CancellationToken ct = default) =>
        PublishCommandAsync(new JsonObject { ["msg"] = "REQUEST-CURRENT-FAULTS" }, ct);

    /// <summary>Starts a whole-home clean with the settings currently configured in the app.</summary>
    public Task StartGlobalCleanAsync(CancellationToken ct = default) =>
        PublishCommandAsync(new JsonObject
        {
            ["msg"] = "START",
            ["mode-reason"] = "RAPP",
            ["cleaningMode"] = "global",
        }, ct);

    public Task PauseAsync(CancellationToken ct = default) =>
        PublishCommandAsync(new JsonObject { ["msg"] = "PAUSE", ["mode-reason"] = "RAPP" }, ct);

    public Task ResumeAsync(CancellationToken ct = default) =>
        PublishCommandAsync(new JsonObject { ["msg"] = "RESUME", ["mode-reason"] = "RAPP" }, ct);

    public Task StopAsync(CancellationToken ct = default) =>
        PublishCommandAsync(new JsonObject { ["msg"] = "STOP", ["mode-reason"] = "RAPP" }, ct);

    /// <summary>Aborts the current clean and sends the robot back to the dock.</summary>
    public async Task ReturnToDockAsync(CancellationToken ct = default)
    {
        await PublishCommandAsync(new JsonObject { ["msg"] = "ABORT", ["mode-reason"] = "RAPP" }, ct).ConfigureAwait(false);
        await PublishJdmAsync("service.start_recharge", ct: ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_client.IsConnected)
                await _client.DisconnectAsync().ConfigureAwait(false);
        }
        catch { /* best effort */ }
        _client.Dispose();
    }
}
