using System.Net;
using System.Net.Http.Headers;
using System.Text;
using DyssCockpit.Core;

namespace DyssCockpit.Core.Tests;

/// <summary>
/// When a request to Dyson is sent again: a read that meets a server error or a 429, up to three
/// times, waiting as Retry-After asks. Driven by a clock moved by hand, so no test waits for real.
/// </summary>
public class DysonCloudClientRetryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AReadIsSentAgainAfterServerErrorsWithGrowingWaits()
    {
        var (api, dyson, clock) = Create(Answer(HttpStatusCode.ServiceUnavailable), Answer(HttpStatusCode.BadGateway), Answer(HttpStatusCode.OK, "[]"));

        var maps = api.GetMapMetadataAsync("SERIAL", Ct);
        Assert.Equal(TimeSpan.FromSeconds(2), await clock.NextDelayAsync());
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(TimeSpan.FromSeconds(4), await clock.NextDelayAsync());
        clock.Advance(TimeSpan.FromSeconds(4));

        Assert.Empty(await maps);
        Assert.Equal(3, dyson.Requests.Count);
        // Each attempt is the same request, still signed.
        Assert.All(dyson.Requests, r => Assert.Equal(("GET", "/v2/app/SERIAL/persistent-map-metadata", "Bearer t"), r));
    }

    [Fact]
    public async Task AReadGivesUpAfterThreeAttempts()
    {
        var (api, dyson, clock) = Create(Answer(HttpStatusCode.InternalServerError));

        var maps = api.GetMapMetadataAsync("SERIAL", Ct);
        clock.Advance(await clock.NextDelayAsync());
        clock.Advance(await clock.NextDelayAsync());

        var ex = await Assert.ThrowsAsync<DysonApiException>(() => maps);
        Assert.Equal(HttpStatusCode.InternalServerError, ex.StatusCode);
        Assert.Equal(3, dyson.Requests.Count);
        Assert.False(clock.HasPendingDelay);
    }

    [Fact]
    public async Task TooManyRequestsWaitsForWhatRetryAfterAsks()
    {
        var (api, dyson, clock) = Create(
            Answer(HttpStatusCode.TooManyRequests, retryAfter: new RetryConditionHeaderValue(TimeSpan.FromSeconds(7))),
            Answer(HttpStatusCode.OK, "[]"));

        var maps = api.GetMapMetadataAsync("SERIAL", Ct);
        Assert.Equal(TimeSpan.FromSeconds(7), await clock.NextDelayAsync());
        clock.Advance(TimeSpan.FromSeconds(7) - TimeSpan.FromMilliseconds(1));
        Assert.Single(dyson.Requests);
        clock.Advance(TimeSpan.FromMilliseconds(1));

        Assert.Empty(await maps);
        Assert.Equal(2, dyson.Requests.Count);
    }

    [Fact]
    public async Task RetryAfterGivenAsADateIsCountedFromNow()
    {
        var clock = new ObservedClock();
        var (api, _, _) = Create(clock,
            Answer(HttpStatusCode.ServiceUnavailable, retryAfter: new RetryConditionHeaderValue(clock.GetUtcNow().AddSeconds(12))),
            Answer(HttpStatusCode.OK, "[]"));

        var maps = api.GetMapMetadataAsync("SERIAL", Ct);
        Assert.Equal(TimeSpan.FromSeconds(12), await clock.NextDelayAsync());
        clock.Advance(TimeSpan.FromSeconds(12));
        Assert.Empty(await maps);
    }

    [Fact]
    public async Task ARetryAfterTooFarAwayIsReportedRatherThanWaitedFor()
    {
        // Hanging a map load for two minutes would be worse than saying it failed.
        var (api, dyson, clock) = Create(Answer(HttpStatusCode.TooManyRequests, retryAfter: new RetryConditionHeaderValue(TimeSpan.FromMinutes(2))));

        var ex = await Assert.ThrowsAsync<DysonApiException>(() => api.GetMapMetadataAsync("SERIAL", Ct));
        Assert.Equal(HttpStatusCode.TooManyRequests, ex.StatusCode);
        Assert.Single(dyson.Requests);
        Assert.False(clock.HasPendingDelay);
    }

    [Fact]
    public async Task AWriteIsNeverSentTwice()
    {
        // A PUT or a POST that failed may still have been applied: sending it again could apply it twice.
        var (api, dyson, clock) = Create(Answer(HttpStatusCode.ServiceUnavailable));

        await Assert.ThrowsAsync<DysonApiException>(() => api.SetMapOrientationAsync("SERIAL", "1", 90, Ct));
        await Assert.ThrowsAsync<DysonApiException>(() => api.GetIotCredentialsAsync("SERIAL", Ct));

        Assert.Equal(["PUT", "POST"], dyson.Requests.Select(r => r.Method));
        Assert.False(clock.HasPendingDelay);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task ClientErrorsAreReportedAtOnce(HttpStatusCode status)
    {
        var (api, dyson, clock) = Create(Answer(status));

        var ex = await Assert.ThrowsAnyAsync<DysonApiException>(() => api.GetMapMetadataAsync("SERIAL", Ct));

        Assert.Equal(status, ex.StatusCode);
        Assert.Equal(status == HttpStatusCode.Unauthorized, ex is DysonAuthException);
        Assert.Single(dyson.Requests);
        Assert.False(clock.HasPendingDelay);
    }

    private static Func<HttpResponseMessage> Answer(HttpStatusCode status, string body = "{}", RetryConditionHeaderValue? retryAfter = null) => () =>
    {
        var resp = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        resp.Headers.RetryAfter = retryAfter;
        return resp;
    };

    private static (DysonCloudClient Api, ScriptedDyson Dyson, ObservedClock Clock) Create(params Func<HttpResponseMessage>[] answers) =>
        Create(new ObservedClock(), answers);

    private static (DysonCloudClient Api, ScriptedDyson Dyson, ObservedClock Clock) Create(ObservedClock clock, params Func<HttpResponseMessage>[] answers)
    {
        var dyson = new ScriptedDyson(answers);
        return (new DysonCloudClient("CH", http: new HttpClient(dyson), time: clock) { BearerToken = "t" }, dyson, clock);
    }

    /// <summary>Answers each request with the next scripted answer, the last one over and over.</summary>
    private sealed class ScriptedDyson(Func<HttpResponseMessage>[] answers) : HttpMessageHandler
    {
        public List<(string Method, string Path, string Authorization)> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            lock (Requests)
            {
                Requests.Add((request.Method.Method, request.RequestUri!.AbsolutePath, request.Headers.Authorization?.ToString() ?? ""));
                return Task.FromResult(answers[Math.Min(Requests.Count, answers.Length) - 1]());
            }
        }
    }
}
