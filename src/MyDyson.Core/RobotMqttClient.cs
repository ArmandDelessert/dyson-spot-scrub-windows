using System.Collections.Concurrent;
using System.Net.Security;
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

    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonObject>> _pendingJdm = new();
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<RobotState>> _pendingState = new();

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
    /// Topic filters subscribed to by default.
    ///
    /// With the custom-authorizer token a wildcard is granted, and it is what we want: it also covers
    /// the command topics, so the requests the official app publishes are captured alongside the
    /// robot's replies. With the IAM (SigV4) credentials only exact filters have a chance, because
    /// AWS IoT closes the connection on any filter the policy does not cover.
    /// </summary>
    public IReadOnlyList<string> DefaultTopicFilters =>
        _endpoint.AuthMode.StartsWith("custom-authorizer")
            ? new[] { $"+/{Serial}/#" }
            : new[] { $"{_prefix}/{Serial}/status", $"{_prefix}/{Serial}/status/jdm" };

    public async Task ConnectAsync(CancellationToken ct = default) =>
        await ConnectAsync(ct, DefaultTopicFilters).ConfigureAwait(false);

    public async Task ConnectAsync(CancellationToken ct, IEnumerable<string> topicFilters)
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
        MessageReceived?.Invoke(message);
        CompletePendingRequests(message);
        return Task.CompletedTask;
    }

    private void CompletePendingRequests(RobotMessage message)
    {
        if (message.Json is not JsonObject json) return;

        if (message.Topic.EndsWith("/status/jdm", StringComparison.Ordinal))
        {
            var id = json["msgId"]?.GetValue<string>();
            if (id is not null && _pendingJdm.TryRemove(id, out var tcs))
                tcs.TrySetResult(json);
        }
        else if (message.Topic.EndsWith("/status", StringComparison.Ordinal)
                 && json["msg"]?.GetValue<string>() == "CURRENT-STATE"
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
            await RequestCurrentStateAsync(ct).ConfigureAwait(false);
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
    public Task PublishJdmAsync(string method, JsonObject? parameters = null, CancellationToken ct = default) =>
        PublishRawAsync(JdmCommandTopic, BuildJdmPayload(method, parameters).ToJsonString(), ct);

    private static JsonObject BuildJdmPayload(string method, JsonObject? parameters) => new()
    {
        // The app uses values in the unsigned 32-bit range.
        ["msgId"] = Random.Shared.NextInt64(1, uint.MaxValue).ToString(),
        ["version"] = "1.0.1",
        ["method"] = method,
        ["params"] = parameters ?? new JsonObject(),
        ["time"] = NowIso(),
    };

    // ---- Commands taken from captures of the official app --------------------
    //
    // Payloads below were observed on 2026-09-19 while the official app drove this robot.
    // The section further down holds commands that only appear as APK strings and are unverified.

    public Task RequestCurrentStateAsync(CancellationToken ct = default) =>
        PublishCommandAsync(new JsonObject { ["msg"] = "REQUEST-CURRENT-STATE" }, ct);

    /// <summary>
    /// Starts a clean of the given zones of a map, which is what the app's start button does.
    /// The app sends the room preferences first with <see cref="SetRoomPreferenceAsync"/>, then this
    /// START, then confirms with <see cref="SetCurrentMapAsync"/> and <see cref="SetRoomCleanAsync"/>.
    /// </summary>
    public Task StartZoneCleanAsync(string persistentMapId, IEnumerable<string> zoneIds, CancellationToken ct = default)
    {
        var zones = new JsonArray();
        foreach (var z in zoneIds) zones.Add(z);
        return PublishCommandAsync(new JsonObject
        {
            ["msg"] = "START",
            ["mode-reason"] = "RAPP",
            ["cleaningMode"] = "zoneConfigured",
            ["fullCleanType"] = "immediate",
            ["cleaningProgramme"] = new JsonObject
            {
                ["persistentMapId"] = persistentMapId,
                ["zonesDefinitionLastUpdatedDate"] = "",
                ["unorderedZones"] = zones,
            },
        }, ct);
    }

    /// <summary>
    /// Stops a dock action such as mop drying, which runs for hours after a clean. Observed with
    /// action DRY_MOP, sent together with <see cref="StartStationActionAsync"/>(0, 2).
    /// </summary>
    public Task AbortDockActionAsync(string action = "DRY_MOP", CancellationToken ct = default) =>
        PublishCommandAsync(new JsonObject
        {
            ["msg"] = "ABORT-DOCK-ACTION",
            ["mode-reason"] = "RAPP",
            ["action"] = action,
        }, ct);

    /// <summary>jdm counterpart of a dock action. Observed as ctrlValue 0, stationAct 2 to stop drying.</summary>
    public Task StartStationActionAsync(int ctrlValue, int stationAct, CancellationToken ct = default) =>
        PublishJdmAsync("service.start_station_act", new JsonObject
        {
            ["ctrl_value"] = ctrlValue,
            ["station_act"] = stationAct,
        }, ct);

    /// <summary>
    /// Sets the robot's own time zone, as an IANA name such as "Europe/Amsterdam". The robot answers
    /// data.result 0 on success and 1 on refusal; it refuses while a dock action is running.
    /// </summary>
    public Task SetRobotTimeZoneAsync(string ianaTimeZone, CancellationToken ct = default) =>
        PublishJdmAsync("service.set_robot_time_zone", new JsonObject { ["time_zone"] = ianaTimeZone }, ct);

    public Task GetMapListAsync(CancellationToken ct = default) =>
        PublishJdmAsync("service.get_map_list", ct: ct);

    public Task GetRoomPreferenceAsync(long mapId, CancellationToken ct = default) =>
        PublishJdmAsync("service.get_preference", new JsonObject { ["map_id"] = mapId }, ct);

    /// <summary>
    /// Sets per-room settings. Rooms are positional arrays, not objects: index 0 is the zone id and
    /// index 1 its name; the rest carry per-room settings and the cleaning order.
    /// </summary>
    public Task SetRoomPreferenceAsync(long mapId, JsonArray roomPreference, JsonArray? uvSwitch = null, CancellationToken ct = default) =>
        PublishJdmAsync("service.set_preference", new JsonObject
        {
            ["map_id"] = mapId,
            ["prefer_type"] = 1,
            ["room_preference"] = roomPreference,
            ["uv_switch"] = uvSwitch ?? new JsonArray(),
        }, ct);

    public Task SetCurrentMapAsync(long mapId, CancellationToken ct = default) =>
        PublishJdmAsync("service.set_cur_map", new JsonObject { ["map_id"] = mapId }, ct);

    public Task SetRoomCleanAsync(IEnumerable<int> roomIds, int cleanType = 0, int ctrlValue = 1, CancellationToken ct = default)
    {
        var ids = new JsonArray();
        foreach (var id in roomIds) ids.Add(id);
        return PublishJdmAsync("service.set_room_clean", new JsonObject
        {
            ["ctrl_value"] = ctrlValue,
            ["clean_type"] = cleanType,
            ["room_ids"] = ids,
        }, ct);
    }

    /// <summary>Reads robot properties. The app always passes an explicit list of names.</summary>
    public Task GetPropertiesAsync(IEnumerable<string>? propertyNames = null, CancellationToken ct = default)
    {
        var names = new JsonArray();
        foreach (var n in propertyNames ?? AppPropertyNames) names.Add(n);
        return PublishJdmAsync("prop.get", new JsonObject { ["property"] = names }, ct);
    }

    /// <summary>The property list the official app requests.</summary>
    public static readonly string[] AppPropertyNames =
    [
        "airdry_frequency", "alarm", "auto_water_complete_flag", "auto_water_self_check",
        "back_wash_time", "back_wash_type", "charge_state", "child_lock", "clean_wash_attachment",
        "cleaning_time", "current_map_id", "detergent", "detergent_consumed", "dock_hypa",
        "dust_auto_state", "empty_bin_time", "empty_bin_type", "fault", "global_clean_status",
        "hot_water_mop", "hot_water_switch", "hypa", "ioniser", "main_brush", "mop_life",
        "mop_pad_life", "oob_state", "quantity", "quiet_begin_time", "quiet_end_time",
        "quiet_is_open", "robot_auto_updown_type", "side_brush", "station_act", "status",
        "store_demo_mode", "sweep_type", "taskBeginTs", "voice_type", "volume",
        "wash_back_frequency", "work_mode",
    ];

    // ---- Pause, abort, mapping (captured 2026-09-19 evening) --------------------

    /// <summary>
    /// Pauses the running clean. The app pairs the classic PAUSE with a jdm set_room_clean whose
    /// ctrl_value is 2 (1 starts, 2 pauses). The robot answers with state FULL_CLEAN_PAUSED.
    /// </summary>
    public async Task PauseAsync(string cleaningMode = "zoneConfigured", CancellationToken ct = default)
    {
        await PublishCommandAsync(new JsonObject
        {
            ["msg"] = "PAUSE",
            ["mode-reason"] = "RAPP",
            ["cleaningMode"] = cleaningMode,
        }, ct).ConfigureAwait(false);
        await PublishJdmAsync("service.set_room_clean", new JsonObject
        {
            ["ctrl_value"] = 2,
            ["clean_type"] = 0,
            ["room_ids"] = new JsonArray(),
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Aborts the current clean and sends the robot home. The classic ABORT carries the robot's
    /// current state (FULL_CLEAN_RUNNING or FULL_CLEAN_PAUSED) and is paired with jdm
    /// service.start_recharge. The robot goes to state ABORTED, then INACTIVE_DISCHARGING or
    /// INACTIVE_CHARGING once docked.
    /// </summary>
    public async Task AbortAsync(string currentState, string cleaningMode = "zoneConfigured", CancellationToken ct = default)
    {
        await PublishCommandAsync(new JsonObject
        {
            ["msg"] = "ABORT",
            ["mode-reason"] = "RAPP",
            ["cleaningMode"] = cleaningMode,
            ["state"] = currentState,
        }, ct).ConfigureAwait(false);
        await PublishJdmAsync("service.start_recharge", ct: ct).ConfigureAwait(false);
    }

    /// <summary>Alias kept for callers that only want the robot home.</summary>
    public Task ReturnToDockAsync(string currentState = "FULL_CLEAN_RUNNING", CancellationToken ct = default) =>
        AbortAsync(currentState, ct: ct);

    /// <summary>
    /// Starts building a new map. The app first sets the map language, then sends START-MAPPING
    /// paired with jdm service.start_explore. States: MAPPING_RUNNING, MAPPING_FINISHED, and the
    /// new persistentMapId appears in CURRENT-STATE once event.BuildMapFinish.post has fired.
    /// </summary>
    public async Task StartMappingAsync(string mapLanguage, CancellationToken ct = default)
    {
        await PublishCommandAsync(new JsonObject
        {
            ["msg"] = "STATE-SET",
            ["mode-reason"] = "RAPP",
            ["mapLanguage"] = mapLanguage,
        }, ct).ConfigureAwait(false);
        await PublishCommandAsync(new JsonObject { ["msg"] = "START-MAPPING", ["mode-reason"] = "RAPP" }, ct).ConfigureAwait(false);
        await PublishJdmAsync("service.start_explore", new JsonObject { ["mode"] = 0 }, ct).ConfigureAwait(false);
    }

    /// <summary>Merges rooms of a map into one. lang 5 is French, as sent by the app.</summary>
    public Task MergeRoomsAsync(long mapId, IEnumerable<int> roomIds, int lang = 5, CancellationToken ct = default)
    {
        var ids = new JsonArray();
        foreach (var id in roomIds) ids.Add(id);
        return PublishJdmAsync("service.arrange_room", new JsonObject
        {
            ["map_id"] = mapId,
            ["room_ids"] = ids,
            ["lang"] = lang,
        }, ct);
    }

    // ---- Commands not observed, taken from APK strings -----------------------

    /// <summary>Starts a clean of the whole map. Not observed: the app uses zones even for a full clean.</summary>
    public Task StartGlobalCleanAsync(CancellationToken ct = default) =>
        PublishCommandAsync(new JsonObject
        {
            ["msg"] = "START",
            ["mode-reason"] = "RAPP",
            ["cleaningMode"] = "global",
            ["fullCleanType"] = "immediate",
        }, ct);

    public Task RequestCurrentFaultsAsync(CancellationToken ct = default) =>
        PublishCommandAsync(new JsonObject { ["msg"] = "REQUEST-CURRENT-FAULTS" }, ct);

    /// <summary>Not observed. The jdm counterpart is probably set_room_clean with another ctrl_value.</summary>
    public Task ResumeAsync(string cleaningMode = "zoneConfigured", CancellationToken ct = default) =>
        PublishCommandAsync(new JsonObject { ["msg"] = "RESUME", ["mode-reason"] = "RAPP", ["cleaningMode"] = cleaningMode }, ct);

    /// <summary>Not observed. The app stops a clean with PAUSE followed by ABORT, never STOP.</summary>
    public Task StopAsync(CancellationToken ct = default) =>
        PublishCommandAsync(new JsonObject { ["msg"] = "STOP", ["mode-reason"] = "RAPP" }, ct);

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
