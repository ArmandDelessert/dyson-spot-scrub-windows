using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MyDyson.Core;

/// <summary>
/// Client for the (unofficial) MyDyson cloud REST API on appapi.cp.dyson.com.
///
/// Login flow (email + password + one-time code sent by email):
///   1. <see cref="ProvisionAsync"/>        GET  /v1/provisioningservice/application/Android/version
///   2. <see cref="GetUserStatusAsync"/>    POST /v3/userregistration/email/userstatus
///   3. <see cref="BeginLoginAsync"/>       POST /v3/userregistration/email/auth      -> challengeId, OTP mail sent
///   4. <see cref="CompleteLoginAsync"/>    POST /v3/userregistration/email/verify    -> bearer token
/// Then, with the bearer token:
///   5. <see cref="GetManifestAsync"/>      GET  /v3/manifest
///   6. <see cref="GetIotCredentialsAsync"/> POST /v2/authorize/iot-credentials       -> AWS IoT websocket credentials
///
/// Note: api.cp.dyson.com is behind Cloudflare mTLS since ~Aug 2026; only appapi.cp.dyson.com works.
/// </summary>
public sealed class DysonCloudClient : IDisposable
{
    public const string DefaultHost = "appapi.cp.dyson.com";
    public const string ChinaHost = "appapi.cp.dyson.cn";

    /// <summary>Options for parsing responses: tolerant about casing.</summary>
    private static readonly JsonSerializerOptions ReadOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Options for request bodies. The naming policy must stay null: Dyson is case-sensitive on
    /// request properties and rejects a camelCased body. POST /v2/authorize/iot-credentials wants
    /// {"Serial": "..."} and answers HTTP 400 "iot-credentials called with incorrect request" for
    /// {"serial": "..."}. Property names are therefore written exactly as declared at the call site.
    /// </summary>
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        PropertyNamingPolicy = null,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    public string Host { get; }
    public string Country { get; }
    public string Culture { get; }
    public string? BearerToken { get; set; }

    public DysonCloudClient(string country, string? culture = null, string host = DefaultHost, HttpClient? http = null)
    {
        Country = country.ToUpperInvariant();
        Culture = culture ?? $"en-{Country}";
        Host = host;
        _ownsHttp = http is null;
        _http = http ?? new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        });
        _http.Timeout = TimeSpan.FromSeconds(30);

        // Headers mimicking the official Android app. Dyson ignores clients with an
        // unexpected User-Agent. Values observed working in September 2026.
        _http.DefaultRequestHeaders.UserAgent.Clear();
        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Dalvik/2.1.0 (Linux; U; Android 11; Build/RQ3A.210905.001)");
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", $"{Culture},en;q=0.9");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("X-App-Version", "6.4.26360");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("X-Platform", "android");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("X-Dyson-LinkApp-Version", "6.4.26360");
    }

    private Uri Url(string path, bool withCountry = true, bool withCulture = false)
    {
        var query = new List<string>();
        if (withCountry) query.Add($"country={Country}");
        if (withCulture) query.Add($"culture={Culture}");
        var q = query.Count > 0 ? "?" + string.Join("&", query) : "";
        return new Uri($"https://{Host}{path}{q}");
    }

    private HttpRequestMessage Request(HttpMethod method, Uri url, object? body = null, bool auth = false)
    {
        var req = new HttpRequestMessage(method, url);
        if (body is not null)
            req.Content = JsonContent.Create(body, options: WriteOptions);
        if (auth)
        {
            if (string.IsNullOrEmpty(BearerToken))
                throw new DysonAuthException("No bearer token; log in first.");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", BearerToken);
        }
        return req;
    }

    private async Task<T> SendAsync<T>(HttpRequestMessage req, CancellationToken ct)
    {
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            var msg = $"{req.Method} {req.RequestUri!.AbsolutePath} failed: HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}";
            if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new DysonAuthException(msg, resp.StatusCode, text);
            throw new DysonApiException(msg, resp.StatusCode, text);
        }
        if (typeof(T) == typeof(string))
            return (T)(object)text;
        try
        {
            return JsonSerializer.Deserialize<T>(text, ReadOptions)
                   ?? throw new DysonApiException("Empty JSON response", resp.StatusCode, text);
        }
        catch (JsonException ex)
        {
            throw new DysonApiException($"Invalid JSON response for {req.RequestUri!.AbsolutePath}", resp.StatusCode, text, ex);
        }
    }

    /// <summary>Must be called before any other request; the server ignores clients that skipped it.</summary>
    public Task<string> ProvisionAsync(CancellationToken ct = default) =>
        SendAsync<string>(Request(HttpMethod.Get, Url("/v1/provisioningservice/application/Android/version", withCountry: false)), ct);

    public Task<UserStatus> GetUserStatusAsync(string email, CancellationToken ct = default) =>
        SendAsync<UserStatus>(Request(HttpMethod.Post, Url("/v3/userregistration/email/userstatus"), new { email }), ct);

    /// <summary>Triggers the one-time code email. The password is NOT sent at this step.</summary>
    public Task<LoginChallenge> BeginLoginAsync(string email, CancellationToken ct = default) =>
        SendAsync<LoginChallenge>(Request(HttpMethod.Post, Url("/v3/userregistration/email/auth", withCulture: true), new { email }), ct);

    public async Task<LoginResult> CompleteLoginAsync(string email, string password, string challengeId, string otpCode, CancellationToken ct = default)
    {
        var body = new { email, password, challengeId, otpCode };
        var result = await SendAsync<LoginResult>(Request(HttpMethod.Post, Url("/v3/userregistration/email/verify", withCulture: true), body), ct).ConfigureAwait(false);
        BearerToken = result.Token;
        return result;
    }

    /// <summary>Lists the devices registered on the account.</summary>
    public async Task<List<Device>> GetManifestAsync(CancellationToken ct = default)
    {
        try
        {
            return await SendAsync<List<Device>>(Request(HttpMethod.Get, Url("/v3/manifest", withCountry: false), auth: true), ct).ConfigureAwait(false);
        }
        catch (DysonApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            // Older endpoint still used by some clients.
            return await SendAsync<List<Device>>(Request(HttpMethod.Get, Url("/v2/provisioningservice/manifest", withCountry: false), auth: true), ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Custom-authorizer token for the AWS IoT broker. Observed to grant subscribe only: publishing
    /// with it makes AWS IoT close the connection. Use <see cref="GetIotRoleCredentialsAsync"/> to
    /// control a robot.
    /// </summary>
    public Task<IotData> GetIotCredentialsAsync(string serial, CancellationToken ct = default) =>
        SendAsync<IotData>(Request(HttpMethod.Post, Url("/v2/authorize/iot-credentials", withCountry: false), new { Serial = serial }, auth: true), ct);

    /// <summary>
    /// Temporary IAM credentials for the AWS IoT broker, as used by the current MyDyson app. These
    /// allow both subscribing and publishing. They are short-lived; check
    /// <see cref="IamCredentials.Expiration"/> and request new ones before reconnecting.
    /// </summary>
    public Task<IotRoleData> GetIotRoleCredentialsAsync(string serial, CancellationToken ct = default) =>
        SendAsync<IotRoleData>(Request(HttpMethod.Post, Url("/v1/authorize/iot-role-credentials", withCountry: false), new { Serial = serial }, auth: true), ct);

    /// <summary>Whether the device is currently connected to Dyson's cloud.</summary>
    public async Task<(string Status, DateTimeOffset? LastChanged)> GetConnectionStatusAsync(string serial, CancellationToken ct = default)
    {
        var json = await SendAsync<JsonElement>(
            Request(HttpMethod.Get, Url($"/v1/messageprocessor/devices/{serial}/connectionstatus", withCountry: false), auth: true), ct)
            .ConfigureAwait(false);
        var status = json.TryGetProperty("Status", out var s) ? s.GetString() ?? "unknown" : "unknown";
        DateTimeOffset? changed = json.TryGetProperty("LastChanged", out var c) && c.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(c.GetString(), out var parsed) ? parsed : null;
        return (status, changed);
    }

    // ---- Maps ------------------------------------------------------------------

    /// <summary>All maps of the device with their zones and per-zone settings. Light, no geometry.</summary>
    public Task<List<MapMetadata>> GetMapMetadataAsync(string serial, CancellationToken ct = default) =>
        SendAsync<List<MapMetadata>>(Request(HttpMethod.Get, Url($"/v2/app/{serial}/persistent-map-metadata", withCountry: false), auth: true), ct);

    /// <summary>
    /// Saves zone settings, selection and order for a map. The body is the zone list as returned by
    /// <see cref="GetMapMetadataAsync"/>, with the fields changed. This is how the app persists the
    /// per-room clean type shown on its map (documented by the ha-dyson-spot-scrub project; the
    /// endpoint exists in the APK as a PUT).
    /// </summary>
    public Task UpdateMapZonesAsync(string serial, string mapId, IReadOnlyList<ZoneMetadata> zones, CancellationToken ct = default) =>
        SendAsync<string>(Request(HttpMethod.Put, Url($"/v2/app/{serial}/persistent-map-metadata/{mapId}", withCountry: false), zones, auth: true), ct);

    /// <summary>One stored map with zone geometry, dock, furniture and restrictions. About 45 KB.</summary>
    public Task<PersistentMap> GetPersistentMapAsync(string serial, string mapId, CancellationToken ct = default) =>
        SendAsync<PersistentMap>(Request(HttpMethod.Get, Url($"/v2/app/{serial}/persistent-maps/{mapId}", withCountry: false), auth: true), ct);

    /// <summary>
    /// Turns a stored map, as the phone's rotate button does: PUT on the map with only
    /// <c>orientation</c> set, in clockwise degrees (0, 90, 180 or 270; the app refuses anything
    /// else). The call and its body were read off the APK's Retrofit interface on 2026-09-28; the
    /// body class also carries name, zone, isCurrentMap and furniture, all optional.
    /// </summary>
    public Task SetMapOrientationAsync(string serial, string mapId, int degrees, CancellationToken ct = default)
    {
        if (degrees is not (0 or 90 or 180 or 270)) throw new ArgumentOutOfRangeException(nameof(degrees), degrees, "0, 90, 180 or 270");
        return SendAsync<string>(Request(HttpMethod.Put, Url($"/v2/app/{serial}/persistent-maps/{mapId}", withCountry: false), new { orientation = degrees }, auth: true), ct);
    }

    /// <summary>
    /// The schedules of the robot's active map, from the cloud's scheduler (see <see cref="ScheduleEvents"/>).
    /// <paramref name="productType"/> is the device type of the manifest, "804" for the RB05; the
    /// service answers 404 without it.
    /// </summary>
    public Task<ScheduleEvents> GetScheduleEventsAsync(string serial, string productType, CancellationToken ct = default) =>
        SendAsync<ScheduleEvents>(Request(HttpMethod.Get, Url($"/v1/unifiedscheduler/{serial}/events?productType={Uri.EscapeDataString(productType)}", withCountry: false), auth: true), ct);

    /// <summary>The current map with robot position and clean path. Works whether or not the robot is cleaning.</summary>
    public Task<LiveMap> GetLiveCleaningMapAsync(string serial, CancellationToken ct = default) =>
        SendAsync<LiveMap>(Request(HttpMethod.Get, Url($"/v1/app/{serial}/live-maps/cleaning", withCountry: false), auth: true), ct);

    /// <summary>The occupancy grid. Large (hundreds of KB); fetch it once, not on a timer.</summary>
    public Task<MappingMap> GetMappingMapAsync(string serial, CancellationToken ct = default) =>
        SendAsync<MappingMap>(Request(HttpMethod.Get, Url($"/v1/app/{serial}/live-maps/mapping", withCountry: false), auth: true), ct);

    // ---- Clean history -----------------------------------------------------------

    /// <summary>Past cleans, newest first. Each carries a presigned S3 link (valid 15 min) to a zlib blob.</summary>
    public async Task<List<CleanSummary>> GetCleanHistoryAsync(string serial, CancellationToken ct = default)
    {
        var list = await SendAsync<CleanList>(Request(HttpMethod.Get, Url($"/v2/{serial}/clean-maps", withCountry: false), auth: true), ct).ConfigureAwait(false);
        return list.Data;
    }

    public Task<CleanDetail> GetCleanDetailAsync(string serial, string cleanId, CancellationToken ct = default) =>
        SendAsync<CleanDetail>(Request(HttpMethod.Get, Url($"/v2/{serial}/clean-maps-data/{cleanId}", withCountry: false), auth: true), ct);

    // ---- Device settings held in the cloud -------------------------------------

    public async Task<string?> GetOtaStatusAsync(string serial, CancellationToken ct = default)
    {
        var json = await SendAsync<JsonElement>(Request(HttpMethod.Get, Url($"/v1/assets/devices/{serial}/ota", withCountry: false), auth: true), ct).ConfigureAwait(false);
        return json.TryGetProperty("otaStatus", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
    }

    public async Task<string?> GetTimeZoneAsync(string serial, CancellationToken ct = default)
    {
        var json = await SendAsync<JsonElement>(Request(HttpMethod.Get, Url($"/v1/machine/{serial}/timezone", withCountry: false), auth: true), ct).ConfigureAwait(false);
        return json.TryGetProperty("timezone", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
    }

    /// <summary>
    /// Sets the time zone through the cloud, which relays it to the robot. On the RB05 tested this
    /// fails with HTTP 424 "Failed to update JDM machine timezone ... Response code: 1", the same
    /// refusal the robot gives to service.set_robot_time_zone over MQTT.
    /// </summary>
    public Task SetTimeZoneAsync(string serial, string ianaTimeZone, CancellationToken ct = default) =>
        SendAsync<string>(Request(HttpMethod.Put, Url($"/v1/machine/{serial}/timezone", withCountry: false), new { timezone = ianaTimeZone }, auth: true), ct);

    /// <summary>Raw authenticated GET, for exploring undocumented endpoints.</summary>
    public Task<string> GetRawAsync(string path, CancellationToken ct = default) =>
        SendAsync<string>(Request(HttpMethod.Get, Url(path, withCountry: false), auth: true), ct);

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}
