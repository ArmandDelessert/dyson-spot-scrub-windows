using System.Text.Json;
using System.Text.Json.Nodes;
using MQTTnet;
using MQTTnet.Diagnostics.Logger;
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
    private readonly MqttEndpoint _endpoint;
    private string _prefix;

    public string Serial { get; }
    public string Prefix => _prefix;
    public bool IsConnected => _client.IsConnected;
    public string AuthMode => _endpoint.AuthMode;

    public event Action<RobotMessage>? MessageReceived;
    public event Action<string>? Disconnected;
    public event Action<string>? PrefixChanged;

    public RobotMqttClient(string serial, string topicPrefix, MqttEndpoint endpoint, Action<string>? traceLogger = null)
    {
        Serial = serial;
        _prefix = topicPrefix;
        _endpoint = endpoint;

        if (traceLogger is null)
        {
            _client = new MqttClientFactory().CreateMqttClient();
        }
        else
        {
            var logger = new MqttNetEventLogger("MyDyson");
            logger.LogMessagePublished += (_, e) =>
            {
                var m = e.LogMessage;
                traceLogger($"{m.Level} {m.Source}: {m.Message}" + (m.Exception is null ? "" : $" | {m.Exception}"));
            };
            _client = new MqttClientFactory(logger).CreateMqttClient();
        }

        _client.ApplicationMessageReceivedAsync += OnMessageAsync;
        _client.DisconnectedAsync += e =>
        {
            var details = $"reason={e.Reason}" +
                          (e.ReasonString is null ? "" : $" reasonString={e.ReasonString}") +
                          $" clientWasConnected={e.ClientWasConnected}" +
                          (e.Exception is null ? "" : $" exception={e.Exception.GetType().Name}: {e.Exception.Message}");
            Disconnected?.Invoke(details);
            return Task.CompletedTask;
        };
    }

    public string CommandTopic => $"{_prefix}/{Serial}/command";
    public string JdmCommandTopic => $"{_prefix}/{Serial}/command/jdm";

    /// <summary>
    /// Topic filters subscribed to by default. Exact filters rather than a wildcard: with the IAM
    /// (SigV4) credentials AWS IoT closes the connection on a filter the policy does not cover.
    /// </summary>
    public IReadOnlyList<string> DefaultTopicFilters => new[]
    {
        $"{_prefix}/{Serial}/status",
        $"{_prefix}/{Serial}/status/jdm",
    };

    public async Task ConnectAsync(CancellationToken ct = default) =>
        await ConnectAsync(ct, DefaultTopicFilters).ConfigureAwait(false);

    public async Task ConnectAsync(CancellationToken ct, IEnumerable<string> topicFilters)
    {
        var options = new MqttClientOptionsBuilder()
            .WithClientId(_endpoint.ClientId)
            .WithWebSocketServer(o => o.WithUri(_endpoint.WebSocketUrl))
            .WithTlsOptions(o => o.UseTls())
            .WithProtocolVersion(MqttProtocolVersion.V311)
            .WithKeepAlivePeriod(TimeSpan.FromSeconds(30))
            .WithCleanSession()
            .Build();

        var result = await _client.ConnectAsync(options, ct).ConfigureAwait(false);
        if (result.ResultCode != MqttClientConnectResultCode.Success)
            throw new DysonApiException($"MQTT connect failed: {result.ResultCode} {result.ReasonString}");

        foreach (var filter in topicFilters)
            await SubscribeAsync(filter, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Subscribes to a topic filter and verifies the broker granted it. AWS IoT answers a denied
    /// subscription with a failure code in the SUBACK instead of an error, so an unchecked
    /// SubscribeAsync looks successful while no message ever arrives.
    /// </summary>
    public async Task SubscribeAsync(string topicFilter, CancellationToken ct = default)
    {
        var result = await _client.SubscribeAsync(topicFilter, MqttQualityOfServiceLevel.AtMostOnce, ct).ConfigureAwait(false);
        foreach (var item in result.Items)
        {
            var granted = item.ResultCode is MqttClientSubscribeResultCode.GrantedQoS0
                or MqttClientSubscribeResultCode.GrantedQoS1
                or MqttClientSubscribeResultCode.GrantedQoS2;
            if (!granted)
                throw new DysonApiException(
                    $"Subscription to '{item.TopicFilter.Topic}' refused by the broker: {item.ResultCode}");
        }
        SubscribedTopics.Add(topicFilter);
    }

    public List<string> SubscribedTopics { get; } = new();

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
