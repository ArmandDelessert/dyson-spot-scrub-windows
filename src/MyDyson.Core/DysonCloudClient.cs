using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

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

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
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
            req.Content = JsonContent.Create(body, options: JsonOptions);
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
            return JsonSerializer.Deserialize<T>(text, JsonOptions)
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

    /// <summary>Short-lived credentials for the AWS IoT MQTT-over-WebSocket broker, for one device.</summary>
    public Task<IotData> GetIotCredentialsAsync(string serial, CancellationToken ct = default) =>
        SendAsync<IotData>(Request(HttpMethod.Post, Url("/v2/authorize/iot-credentials", withCountry: false), new { Serial = serial }, auth: true), ct);

    /// <summary>Raw authenticated GET, for exploring undocumented endpoints.</summary>
    public Task<string> GetRawAsync(string path, CancellationToken ct = default) =>
        SendAsync<string>(Request(HttpMethod.Get, Url(path, withCountry: false), auth: true), ct);

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}
