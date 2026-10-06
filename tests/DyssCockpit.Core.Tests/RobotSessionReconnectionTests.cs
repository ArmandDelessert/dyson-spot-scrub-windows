using System.Net;
using System.Text;
using System.Threading.Channels;
using DyssCockpit.Core;
using Microsoft.Extensions.Logging;

namespace DyssCockpit.Core.Tests;

/// <summary>
/// The reconnection loop of <see cref="RobotSession"/>, driven without a broker or a single real
/// second: a fake link stands in for MQTT, canned HTTP answers for the credentials, and a clock
/// that only moves when told to. Each wait the session starts is observed as it is set, so the
/// tests know exactly what is pending before they move the clock.
/// </summary>
public class RobotSessionReconnectionTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task ADroppedLinkIsReplacedOnceTheBackoffHasRunOut()
    {
        await using var robot = new Harness();
        await robot.ConnectAsync();
        Assert.Equal([RobotConnectionStatus.Connecting, RobotConnectionStatus.Connected], await robot.NextStatusesAsync(2));

        robot.Links[0].Drop("wifi");
        Assert.Equal((RobotConnectionStatus.Reconnecting, "wifi"), await robot.NextStatusAsync());
        Assert.Equal(TimeSpan.FromSeconds(5), await robot.Clock.NextDelayAsync());

        // Nothing is attempted a moment before the wait is over...
        robot.Clock.Advance(TimeSpan.FromSeconds(5) - TimeSpan.FromMilliseconds(1));
        Assert.Single(robot.Links);
        Assert.Equal(1, robot.Credentials.Requests);

        // ...and everything once it is: fresh credentials, a new link, the old one closed.
        robot.Clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(RobotConnectionStatus.Reconnecting, (await robot.NextStatusAsync()).Status);
        Assert.Equal(RobotConnectionStatus.Connected, (await robot.NextStatusAsync()).Status);
        Assert.Equal(2, robot.Links.Count);
        Assert.Equal(2, robot.Credentials.Requests);
        Assert.True(robot.Links[0].Disposed);
        Assert.False(robot.Links[1].Disposed);
    }

    [Fact]
    public async Task FailedAttemptsBackOffUpToTwoMinutesThenStartOverOnceConnected()
    {
        await using var robot = new Harness();
        await robot.ConnectAsync();
        await robot.NextStatusesAsync(2);

        robot.FailNextConnections(6);
        robot.Links[0].Drop("broker gone");
        await robot.NextStatusAsync();

        int[] expected = [5, 10, 20, 40, 80, 120, 120];
        foreach (var seconds in expected)
        {
            var delay = await robot.Clock.NextDelayAsync();
            Assert.Equal(TimeSpan.FromSeconds(seconds), delay);
            robot.Clock.Advance(delay);
        }
        // The seventh attempt was let through.
        await robot.WaitForStatusAsync(RobotConnectionStatus.Connected);
        Assert.Equal(1 + expected.Length, robot.Credentials.Requests);

        // A later drop starts again from five seconds, not from where the last outage left off.
        robot.Links[^1].Drop("again");
        await robot.WaitForStatusAsync(RobotConnectionStatus.Reconnecting);
        Assert.Equal(TimeSpan.FromSeconds(5), await robot.Clock.NextDelayAsync());
    }

    [Fact]
    public async Task ARefusedTokenEndsTheSessionInsteadOfRetryingForever()
    {
        await using var robot = new Harness();
        await robot.ConnectAsync();
        await robot.NextStatusesAsync(2);
        var lost = new List<string>();
        robot.Session.AuthenticationLost += lost.Add;

        robot.Credentials.Status = HttpStatusCode.Unauthorized;
        robot.Links[0].Drop("token expired");
        robot.Clock.Advance(await robot.Clock.NextDelayAsync());

        await robot.WaitForStatusAsync(RobotConnectionStatus.Disconnected);
        Assert.Single(lost);
        // However long the app then stays open, nothing more is tried.
        robot.Clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(2, robot.Credentials.Requests);
        Assert.Single(robot.Links);
    }

    [Fact]
    public async Task ADropFromALinkAlreadyReplacedIsIgnored()
    {
        await using var robot = new Harness();
        await robot.ConnectAsync();
        robot.Links[0].Drop("first");
        robot.Clock.Advance(await robot.Clock.NextDelayAsync());
        await robot.WaitForStatusAsync(RobotConnectionStatus.Connected);

        // The old link's last gasp, arriving late, must not start a second reconnection.
        robot.Links[0].Drop("late");
        robot.Clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(2, robot.Links.Count);
        Assert.Equal(RobotConnectionStatus.Connected, robot.Session.Status);
    }

    [Fact]
    public async Task DisposingStopsAPendingReconnection()
    {
        var robot = new Harness();
        await robot.ConnectAsync();
        robot.Links[0].Drop("wifi");
        await robot.Clock.NextDelayAsync();

        await robot.DisposeAsync();
        robot.Clock.Advance(TimeSpan.FromHours(1));

        Assert.Single(robot.Links);
        Assert.Equal(1, robot.Credentials.Requests);
        Assert.Equal(RobotConnectionStatus.Disconnected, robot.Session.Status);
    }

    [Fact]
    public async Task TheJournalLinesSpeakTheApplicationsLanguage()
    {
        // Shown as they are in the Journal tab, next to the dashboard's own lines.
        await using var robot = new Harness();
        await robot.ConnectAsync();
        await robot.NextStatusesAsync(2);

        robot.Links[0].Drop("wifi");
        await robot.Clock.NextDelayAsync();

        Assert.Contains((LogLevel.Warning, "MQTT coupé (wifi) ; reconnexion dans 5 s (tentative 1)"), robot.Log.Lines);
    }

    /// <summary>A session wired to fakes, with what it did recorded for the assertions.</summary>
    private sealed class Harness : IAsyncDisposable
    {
        private readonly Channel<(RobotConnectionStatus, string?)> _statuses = Channel.CreateUnbounded<(RobotConnectionStatus, string?)>();
        private readonly DysonCloudClient _api;
        private int _failuresLeft;

        public Harness()
        {
            _api = new DysonCloudClient("CH", http: new HttpClient(Credentials)) { BearerToken = "t" };
            var device = new Device("SERIAL-1", "Robot", "804", "RB05", "robot", null, null, null);
            Session = new RobotSession(_api, device, Log, Clock, OpenLink);
            Session.ConnectionChanged += (s, d) => _statuses.Writer.TryWrite((s, d));
        }

        public ObservedClock Clock { get; } = new();
        public RecordingLogger Log { get; } = new();
        public CredentialsEndpoint Credentials { get; } = new();
        public RobotSession Session { get; }
        /// <summary>Every link the session opened, oldest first.</summary>
        public List<FakeLink> Links { get; } = [];

        public Task ConnectAsync() => Session.ConnectAsync(TestContext.Current.CancellationToken);

        public void FailNextConnections(int count) => Volatile.Write(ref _failuresLeft, count);

        private FakeLink OpenLink(IotData iot, string prefix)
        {
            var fail = Interlocked.Decrement(ref _failuresLeft) >= 0;
            var link = new FakeLink(fail);
            lock (Links) Links.Add(link);
            return link;
        }

        public async Task<(RobotConnectionStatus Status, string? Detail)> NextStatusAsync() =>
            await _statuses.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(Patience, TestContext.Current.CancellationToken);

        public async Task<RobotConnectionStatus[]> NextStatusesAsync(int count)
        {
            var list = new RobotConnectionStatus[count];
            for (var i = 0; i < count; i++) list[i] = (await NextStatusAsync()).Status;
            return list;
        }

        public async Task WaitForStatusAsync(RobotConnectionStatus status)
        {
            while ((await NextStatusAsync()).Status != status) { }
        }

        public async ValueTask DisposeAsync()
        {
            await Session.DisposeAsync();
            _api.Dispose();
        }
    }

    /// <summary>POST /v2/authorize/iot-credentials, answering with <see cref="Status"/>.</summary>
    private sealed class CredentialsEndpoint : HttpMessageHandler
    {
        private int _requests;

        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public int Requests => Volatile.Read(ref _requests);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref _requests);
            return Task.FromResult(new HttpResponseMessage(Status)
            {
                Content = new StringContent("""
                    {"Endpoint":"host","IoTCredentials":{"ClientId":"c","TokenKey":"token","TokenValue":"v","TokenSignature":"s","CustomAuthorizerName":"a"}}
                    """, Encoding.UTF8, "application/json"),
            });
        }
    }

    /// <summary>The broker connection as the session sees it: it connects, or refuses to, and drops when told.</summary>
    private sealed class FakeLink(bool refuse) : IRobotLink
    {
        public bool IsConnected { get; private set; }
        public bool Disposed { get; private set; }

        public event Action<RobotMessage>? MessageReceived { add { } remove { } }
        public event Action<Exception>? ListenerFailed { add { } remove { } }
        public event Action<string>? PrefixChanged { add { } remove { } }
        public event Action<string>? Disconnected;

        public Task ConnectAsync(CancellationToken ct = default)
        {
            if (refuse) throw new IOException("broker unreachable");
            IsConnected = true;
            return Task.CompletedTask;
        }

        public void Drop(string reason)
        {
            IsConnected = false;
            Disconnected?.Invoke(reason);
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            IsConnected = false;
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>Keeps what was logged, with its level, for the assertions.</summary>
internal sealed class RecordingLogger : ILogger
{
    private readonly List<(LogLevel, string)> _lines = [];

    public IReadOnlyList<(LogLevel Level, string Text)> Lines
    {
        get { lock (_lines) return [.. _lines]; }
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (_lines) _lines.Add((logLevel, formatter(state, exception)));
    }
}
