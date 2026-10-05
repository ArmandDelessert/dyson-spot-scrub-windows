using System.Threading.Channels;
using Dyss.Core;
using Dyss.Presentation.Services;
using Dyss.Presentation.ViewModels;
using Microsoft.Extensions.Time.Testing;

namespace Dyss.Presentation.Tests;

public class MainViewModelTests
{
    private const string Manifest = """[{"serialNumber":"SERIAL-1","name":"Robot","type":"804","category":"robot"}]""";

    [Fact]
    public async Task AnUnreachableBrokerIsTriedAgainOnTheBackoffWithoutHangingTheDashboard()
    {
        // No route for the broker's credentials: every first connection fails as it would offline.
        var handler = new TestHub.RouteHandler(("/v3/manifest", Manifest));
        var api = new DysonCloudClient("CH", "fr-CH", http: new HttpClient(handler)) { BearerToken = "test-token" };
        var stored = new StoredSession("test@example.invalid", "CH", "fr-CH", null, "test-token", DateTimeOffset.UnixEpoch);
        var ctx = RobotContext.FromLogin(api, stored);
        await ctx.LoadDevicesAsync(ct: TestContext.Current.CancellationToken);
        var clock = new ObservedClock();
        using var vm = new MainViewModel(ctx, new TestHub.InlineDispatcher(), new FakeDialogs(), clock, new DisplaySettings());
        int CredentialRequests() => handler.Requested.Count(p => p.Contains("iot-credentials", StringComparison.Ordinal));

        var start = vm.StartAsync();

        Assert.Equal(TimeSpan.FromSeconds(5), await clock.NextDelayAsync());
        Assert.Contains("5 s", vm.Hub.Connection, StringComparison.Ordinal);
        Assert.Equal(1, CredentialRequests());

        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(TimeSpan.FromSeconds(10), await clock.NextDelayAsync());
        Assert.Equal(2, CredentialRequests());
        Assert.Contains("10 s", vm.Hub.Connection, StringComparison.Ordinal);

        // Closing the window ends the wait at once instead of after the remaining ten seconds.
        await vm.ShutdownAsync();
        await start.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(2, CredentialRequests());
    }

    [Fact]
    public void TheNotificationAreaSummarySaysWhatTheRobotIsDoingAndFlagsAFault()
    {
        var api = new DysonCloudClient("CH", "fr-CH", http: new HttpClient(new TestHub.RouteHandler())) { BearerToken = "test-token" };
        var stored = new StoredSession("test@example.invalid", "CH", "fr-CH", null, "test-token", DateTimeOffset.UnixEpoch);
        using var vm = new MainViewModel(RobotContext.FromLogin(api, stored), new TestHub.InlineDispatcher(), new FakeDialogs(), display: new DisplaySettings());
        var changes = 0;
        vm.SummaryChanged += () => changes++;

        Assert.Equal("Robot : Connexion…", vm.Summary);

        vm.Hub.Connected = true;
        vm.Status.Apply(RobotState.Parse("""{"msg":"CURRENT-STATE","state":"INACTIVE_CHARGING","batteryChargeLevel":100}""")!);
        Assert.Equal("Robot : En charge sur la station · 100 %", vm.Summary);
        Assert.False(vm.NeedsAttention);

        vm.Status.Apply(RobotState.Parse("""{"msg":"CURRENT-STATE","state":"FULL_CLEAN_RUNNING","batteryChargeLevel":80,"activeFaults":[{"faultCode":"589","nextActionRequired":"WAIT_TO_CLEAR"}]}""")!);
        Assert.Equal("Robot : Nettoyage en cours · 80 % · faute 589 (localisation impossible)", vm.Summary);
        Assert.True(vm.NeedsAttention);
        Assert.True(changes >= 3);
    }

    /// <summary>A clock that only moves when told to, and reports each wait set on it.</summary>
    private sealed class ObservedClock : FakeTimeProvider
    {
        private readonly Channel<TimeSpan> _delays = Channel.CreateUnbounded<TimeSpan>();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = base.CreateTimer(callback, state, dueTime, period);
            _delays.Writer.TryWrite(dueTime);
            return timer;
        }

        public async Task<TimeSpan> NextDelayAsync() =>
            await _delays.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask()
                .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }
}
