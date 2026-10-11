using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Security;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Diagnostics.Logger;
using MQTTnet.Formatter;
using MQTTnet.Protocol;

namespace DyssCockpit.Core;

public sealed record RobotMessage(DateTimeOffset ReceivedUtc, string Topic, string Payload)
{
    /// <summary>
    /// Parsed once, here, rather than on every access: the client, the tracker and the capture all
    /// read it for each message, and Kind alone used to parse twice. Shared, so consumers must not
    /// mutate it or attach it to another tree (JdmProperties.Merge clones for that reason).
    /// </summary>
    public JsonNode? Json { get; } = ParseOrNull(Payload);

    /// <summary>
    /// "msg" for classic Dyson messages, "method" for the JDM (JSON-RPC-like) layer. Null for
    /// anything else, including a payload that is not a JSON object or a field that is not a string.
    /// </summary>
    public string? Kind => Json is JsonObject o ? StringOf(o["msg"]) ?? StringOf(o["method"]) : null;

    internal static string? StringOf(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static JsonNode? ParseOrNull(string payload)
    {
        try { return JsonNode.Parse(payload); }
        catch (JsonException) { return null; }
    }
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
public sealed partial class RobotMqttClient : IRobotLink, IRobotCommands
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
    /// <summary>A <see cref="MessageReceived"/> listener threw; the message still completed any request waiting for it.</summary>
    public event Action<Exception>? ListenerFailed;
    public event Action<string>? PrefixChanged;

    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonObject>> _pendingJdm = new();
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<RobotState>> _pendingState = new();

    /// <param name="trace">Receives MQTTnet's own trace, for looking into a connection that fails; null leaves it off.</param>
    public RobotMqttClient(string serial, string topicPrefix, MqttEndpoint endpoint, ILogger? trace = null)
    {
        Serial = serial;
        _prefix = topicPrefix;
        _endpoint = endpoint;

        if (trace is null)
        {
            _client = new MqttClientFactory().CreateMqttClient();
        }
        else
        {
            var logger = new MqttNetEventLogger("Dyss");
            logger.LogMessagePublished += (_, e) =>
            {
                var m = e.LogMessage;
                LogMqttTrace(trace, m.Level, m.Source, m.Message, m.Exception);
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

    [LoggerMessage(Level = LogLevel.Debug, Message = "{MqttLevel} {Source}: {Text}")]
    private static partial void LogMqttTrace(ILogger logger, MqttNetLogLevel mqttLevel, string source, string text, Exception? exception);

    public string CommandTopic => $"{_prefix}/{Serial}/command";
    public string JdmCommandTopic => $"{_prefix}/{Serial}/command/jdm";

    /// <summary>
    /// Topic filters subscribed to by default.
    ///
    /// With the custom-authorizer token a wildcard is granted, and it is what we want: it also covers
    /// the command topics, so the requests the official app publishes are captured alongside the
    /// robot's replies. With the IAM (SigV4) credentials only exact filters have a chance, because
    /// AWS IoT closes the connection on any filter the policy does not cover.
    /// </summary>
    public IReadOnlyList<string> DefaultTopicFilters =>
        _endpoint.AuthMode.StartsWith("custom-authorizer", StringComparison.Ordinal)
            ? new[] { $"+/{Serial}/#" }
            : new[] { $"{_prefix}/{Serial}/status", $"{_prefix}/{Serial}/status/jdm" };

    public async Task ConnectAsync(CancellationToken ct = default) =>
        await ConnectAsync(DefaultTopicFilters, ct).ConfigureAwait(false);

    public async Task ConnectAsync(IEnumerable<string> topicFilters, CancellationToken ct = default)
    {
        var builder = new MqttClientOptionsBuilder()
            .WithClientId(_endpoint.ClientId)
            .WithProtocolVersion(MqttProtocolVersion.V311)
            .WithCleanSession();

        if (_endpoint.UsesWebSocket)
        {
            builder
                .WithWebSocketServer(o => o.WithUri(_endpoint.WebSocketUrl!))
                .WithTlsOptions(o => o.UseTls())
                .WithKeepAlivePeriod(TimeSpan.FromSeconds(30));
        }
        else
        {
            // The official app's transport: TLS on 443 with ALPN "mqtt", credentials in the username.
            builder
                .WithTcpServer(_endpoint.Endpoint, 443)
                .WithTlsOptions(o => o
                    .UseTls()
                    .WithTargetHost(_endpoint.Endpoint)
                    .WithRevocationMode(System.Security.Cryptography.X509Certificates.X509RevocationMode.NoCheck)
                    .WithApplicationProtocols([new SslApplicationProtocol("mqtt")])
                    .WithCertificateValidationHandler(args =>
                    {
                        // Standard validation, but with the reason for a rejection made visible.
                        if (args.SslPolicyErrors != System.Net.Security.SslPolicyErrors.None)
                        {
                            var chain = args.Chain is null ? "" :
                                string.Join("; ", args.Chain.ChainStatus.Select(s => $"{s.Status}: {s.StatusInformation.Trim()}"));
                            TlsRejection = $"{args.SslPolicyErrors} subject={args.Certificate?.Subject} chain=[{chain}]";
                            return false;
                        }
                        return true;
                    }))
                .WithKeepAlivePeriod(TimeSpan.FromSeconds(300));
            if (_endpoint.Username is not null)
                builder.WithCredentials(_endpoint.Username, (string?)null);
        }

        var options = builder.Build();

        MqttClientConnectResult result;
        try
        {
            result = await _client.ConnectAsync(options, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (TlsRejection is not null)
        {
            throw new DysonApiException($"TLS certificate rejected: {TlsRejection}", inner: ex);
        }
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

    /// <summary>Why the last TLS handshake rejected the server certificate, if it did.</summary>
    public string? TlsRejection { get; private set; }

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

        var message = new RobotMessage(DateTimeOffset.UtcNow, topic, payload);
        // A listener that throws must not keep the reply from reaching whoever is waiting for it,
        // nor surface in MQTTnet's receive loop.
        try { MessageReceived?.Invoke(message); }
        catch (Exception ex) { ListenerFailed?.Invoke(ex); }
        try { CompletePendingRequests(message); }
        catch (Exception ex) { ListenerFailed?.Invoke(ex); }
        return Task.CompletedTask;
    }

    private void CompletePendingRequests(RobotMessage message)
    {
        if (message.Json is not JsonObject json) return;

        if (message.Topic.EndsWith("/status/jdm", StringComparison.Ordinal))
        {
            var id = RobotMessage.StringOf(json["msgId"]);
            if (id is not null && _pendingJdm.TryRemove(id, out var tcs))
                tcs.TrySetResult(json);
        }
        else if (message.Topic.EndsWith("/status", StringComparison.Ordinal)
                 && RobotMessage.StringOf(json["msg"]) == "CURRENT-STATE"
                 && RobotState.Parse(message.Payload) is { IsPositionOnly: false } state)
        {
            foreach (var key in _pendingState.Keys)
                if (_pendingState.TryRemove(key, out var tcs))
                    tcs.TrySetResult(state);
        }
    }

    /// <summary>Publishes REQUEST-CURRENT-STATE and waits for the full CURRENT-STATE that answers it.</summary>
    public async Task<RobotState> RequestStateAsync(TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var key = Guid.NewGuid();
        var tcs = new TaskCompletionSource<RobotState>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingState[key] = tcs;
        try
        {
            await this.RequestCurrentStateAsync(ct).ConfigureAwait(false);
            return await WaitAsync(tcs.Task, timeout ?? TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
        }
        finally
        {
            _pendingState.TryRemove(key, out _);
        }
    }

    /// <summary>
    /// Publishes a jdm request and waits for the reply carrying the same msgId. The reply's
    /// data.result (0 success, 1 refused) is the verdict for service.set_* methods; the envelope
    /// code is not reliable (prop.get answers code 1 with valid data).
    /// </summary>
    public async Task<JsonObject> RequestJdmAsync(string method, JsonObject? parameters = null, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var payload = BuildJdmPayload(method, parameters);
        var id = payload["msgId"]!.GetValue<string>();
        var tcs = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingJdm[id] = tcs;
        try
        {
            await PublishRawAsync(JdmCommandTopic, payload.ToJsonString(), ct).ConfigureAwait(false);
            return await WaitAsync(tcs.Task, timeout ?? TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
        }
        finally
        {
            _pendingJdm.TryRemove(id, out _);
        }
    }

    private static async Task<T> WaitAsync<T>(Task<T> task, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            return await task.WaitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"No reply from the robot within {timeout.TotalSeconds:F0} s.");
        }
    }

    private static string NowIso() => DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

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
    public Task PublishJdmAsync(string method, JsonObject? parameters = null, CancellationToken ct = default) =>
        PublishRawAsync(JdmCommandTopic, BuildJdmPayload(method, parameters).ToJsonString(), ct);

    private static JsonObject BuildJdmPayload(string method, JsonObject? parameters) => new()
    {
        // The app uses values in the unsigned 32-bit range.
        ["msgId"] = Random.Shared.NextInt64(1, uint.MaxValue).ToString(CultureInfo.InvariantCulture),
        ["version"] = "1.0.1",
        ["method"] = method,
        ["params"] = parameters ?? new JsonObject(),
        ["time"] = NowIso(),
    };

    public async ValueTask DisposeAsync()
    {
        // Both a graceful MQTT DISCONNECT and the client's own Dispose() have been observed to hang
        // against this broker for minutes, CPU idle throughout — clearly stuck waiting on the network
        // rather than doing anything, and not something a CancellationToken passed to DisconnectAsync
        // reliably cuts short. Never worth blocking app shutdown on: run the whole best-effort cleanup
        // on its own thread and simply stop waiting on it after a few seconds. If it does eventually
        // finish in the background that's fine; if it never does, it can't hold up the process, since
        // thread-pool threads don't prevent exit the way a foreground Thread would.
        var cleanup = Task.Run(() =>
        {
            try
            {
                if (_client.IsConnected) _client.DisconnectAsync().GetAwaiter().GetResult();
            }
            catch { /* best effort */ }
            finally
            {
                try { _client.Dispose(); } catch { /* best effort */ }
            }
        });
        await Task.WhenAny(cleanup, Task.Delay(TimeSpan.FromSeconds(3))).ConfigureAwait(false);
    }
}
