using System.Net;
using System.Text;
using Dyss.Core;

namespace Dyss.Core.Tests;

public class DysonCloudClientTests
{
    /// <summary>Records every request and answers with canned JSON.</summary>
    private sealed class CapturingHandler(string responseJson) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string? Body)> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            Requests.Add((request, body));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json"),
            };
        }
    }

    [Fact]
    public async Task IotCredentialsRequestKeepsPascalCaseSerial()
    {
        // Dyson rejects {"serial": ...} with HTTP 400; the property must be sent as "Serial".
        var handler = new CapturingHandler("""
            {"Endpoint":"host","IoTCredentials":{"ClientId":"c","TokenKey":"token","TokenValue":"v","TokenSignature":"s","CustomAuthorizerName":"a"}}
            """);
        using var api = new DysonCloudClient("CH", http: new HttpClient(handler)) { BearerToken = "t" };

        await api.GetIotCredentialsAsync("SERIAL-1");

        var (request, body) = Assert.Single(handler.Requests);
        Assert.Equal("""{"Serial":"SERIAL-1"}""", body);
        Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
        Assert.Equal("/v2/authorize/iot-credentials", request.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task LoginBodiesUseTheServersCamelCaseNames()
    {
        var handler = new CapturingHandler("""{"account":"acc","token":"tok","tokenType":"Bearer"}""");
        using var api = new DysonCloudClient("CH", "fr-CH", http: new HttpClient(handler));

        await api.CompleteLoginAsync("me@example.org", "pw", "challenge", "123456");

        var (request, body) = Assert.Single(handler.Requests);
        Assert.Equal("""{"email":"me@example.org","password":"pw","challengeId":"challenge","otpCode":"123456"}""", body);
        Assert.Equal("country=CH&culture=fr-CH", request.RequestUri!.Query.TrimStart('?'));
        Assert.Equal("tok", api.BearerToken);
    }

    [Fact]
    public async Task ResponsesAreParsedCaseInsensitively()
    {
        var handler = new CapturingHandler("""{"endpoint":"h","region":"eu-west-1","iamCredentials":{"accessKeyId":"AK","secretAccessKey":"SK","sessionToken":"ST","expiration":"2026-09-19T12:00:00+00:00"}}""");
        using var api = new DysonCloudClient("CH", http: new HttpClient(handler)) { BearerToken = "t" };

        var role = await api.GetIotRoleCredentialsAsync("S");

        Assert.Equal("AK", role.IamCredentials.AccessKeyId);
        Assert.Equal("eu-west-1", role.Region);
    }

    [Fact]
    public async Task UnauthorizedBecomesAuthException()
    {
        var handler = new StatusHandler(HttpStatusCode.Unauthorized, """{"message":"nope"}""");
        using var api = new DysonCloudClient("CH", http: new HttpClient(handler)) { BearerToken = "t" };

        var ex = await Assert.ThrowsAsync<DysonAuthException>(() => api.GetManifestAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, ex.StatusCode);
        Assert.Contains("nope", ex.ResponseBody);
    }

    private sealed class StatusHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }
}
