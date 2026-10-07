using System.Threading.Channels;
using DyssCockpit.Core;
using DyssCockpit.Presentation.Services;
using DyssCockpit.Presentation.ViewModels;
using Microsoft.Extensions.Time.Testing;

namespace DyssCockpit.Presentation.Tests;

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
        using var vm = new MainViewModel(ctx, new TestHub.InlineDispatcher(), new FakeDialogs(), clock, new AppSettings());
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
        using var vm = new MainViewModel(RobotContext.FromLogin(api, stored), new TestHub.InlineDispatcher(), new FakeDialogs(), settings: new AppSettings());
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

    private static MainViewModel NewDashboard(AppSettings settings, out List<(string Title, string Body)> notifications, TimeProvider? time = null)
    {
        var api = new DysonCloudClient("CH", "fr-CH", http: new HttpClient(new TestHub.RouteHandler())) { BearerToken = "test-token" };
        var stored = new StoredSession("test@example.invalid", "CH", "fr-CH", null, "test-token", DateTimeOffset.UnixEpoch);
        var vm = new MainViewModel(RobotContext.FromLogin(api, stored), new TestHub.InlineDispatcher(), new FakeDialogs(), time, settings);
        var sent = new List<(string, string)>();
        vm.NotifyRequested += (title, body) => sent.Add((title, body));
        notifications = sent;
        return vm;
    }

    private static readonly System.Text.Json.Nodes.JsonObject NoParameters = new();

    private static RobotState State(string state, string mode = "zoneConfigured") =>
        RobotState.Parse($$$"""{"msg":"CURRENT-STATE","state":"{{{state}}}","currentCleaningMode":"{{{mode}}}"}""")!;

    private static long s_reports;

    /// <summary>The robot's report of a task, each with a start time of its own, as the robot's are: a report seen twice counts once.</summary>
    private static System.Text.Json.Nodes.JsonObject Report(int status, int minutes = 0, int mode = 0) =>
        System.Text.Json.Nodes.JsonNode.Parse($$$"""{"method":"event.clean_record.post","params":{"record_start_time":{{{Interlocked.Increment(ref s_reports)}}},"record_task_status":{{{status}}},"record_clean_mode":{{{mode}}},"record_use_time":{{{minutes}}}}}""")!.AsObject();

    /// <summary>A clean from the robot waiting to the robot done, as the captures show it.</summary>
    private static void PlayClean(MainViewModel vm, int minutes = 8, string mode = "zoneConfigured")
    {
        vm.OnRobotState(State("INACTIVE_CHARGING", "global"));
        vm.OnRobotEvent("event.startClean.post", NoParameters);
        vm.OnRobotState(State("FULL_CLEAN_RUNNING", mode));
        vm.OnRobotState(State("FULL_CLEAN_FINISHED", mode));
        vm.OnRobotEvent("event.clean_finish.post", NoParameters);
        vm.OnRobotEvent("event.clean_record.post", Report(status: 1, minutes));
    }

    [Fact]
    public void ByDefaultOnlyTheEndOfACleanIsTold()
    {
        using var vm = NewDashboard(new AppSettings(), out var notifications);

        PlayClean(vm, minutes: 8);

        var (title, body) = Assert.Single(notifications);
        Assert.Equal("Nettoyage terminé", title);
        Assert.Equal("Le robot a terminé son nettoyage en 8 min.", body);
    }

    [Fact]
    public void TheStartIsToldTooWhenTheSettingAsksForIt()
    {
        using var vm = NewDashboard(new AppSettings { TaskNotifications = TaskNotificationMode.StartAndEnd }, out var notifications);

        PlayClean(vm);

        Assert.Equal(["Nettoyage commencé", "Nettoyage terminé"], notifications.Select(n => n.Title));
        Assert.Equal("Le robot a commencé son nettoyage.", notifications[0].Body);
    }

    [Fact]
    public void NothingIsToldOfACleanWhenTheSettingIsNone()
    {
        using var vm = NewDashboard(new AppSettings { TaskNotifications = TaskNotificationMode.None }, out var notifications);

        PlayClean(vm);
        vm.OnRobotState(State("INACTIVE_CHARGING", "global"));
        vm.OnRobotState(State("FULL_CLEAN_RUNNING"));
        vm.OnRobotEvent("event.clean_record.post", Report(status: 4));

        Assert.Empty(notifications);
    }

    [Fact]
    public void ACleanTheRobotGivesUpIsToldAsInterruptedAndNotAsFinished()
    {
        using var vm = NewDashboard(new AppSettings(), out var notifications);

        vm.OnRobotState(State("INACTIVE_CHARGING", "global"));
        vm.OnRobotState(State("FULL_CLEAN_RUNNING"));
        vm.OnRobotEvent("event.locate_fail.post", NoParameters);
        vm.OnRobotEvent("event.clean_record.post", Report(status: 4));

        var (title, body) = Assert.Single(notifications);
        Assert.Equal("Nettoyage interrompu", title);
        Assert.Equal("Le robot a abandonné son nettoyage.", body);
    }

    [Fact]
    public void ACleanTheUserStopsOrAMappingTellsNothing()
    {
        using var vm = NewDashboard(new AppSettings(), out var notifications);

        vm.OnRobotState(State("INACTIVE_CHARGING", "global"));
        vm.OnRobotState(State("FULL_CLEAN_RUNNING"));
        vm.OnRobotEvent("event.clean_record.post", Report(status: 2));
        vm.OnRobotState(State("ABORTED", "global"));
        vm.OnRobotEvent("event.startBuildMap.post", NoParameters);
        vm.OnRobotState(State("MAPPING_RUNNING"));
        vm.OnRobotState(State("MAPPING_FINISHED"));
        vm.OnRobotEvent("event.clean_record.post", Report(status: 1, minutes: 1, mode: 4));

        Assert.Empty(notifications);
    }

    [Fact]
    public void AnUnreachableAreaIsToldOnItsOwnAndThenTheCleanIsToldInterrupted()
    {
        using var vm = NewDashboard(new AppSettings(), out var notifications);

        vm.OnRobotState(State("INACTIVE_CHARGING", "global"));
        vm.OnRobotState(State("FULL_CLEAN_RUNNING"));
        vm.OnRobotEvent("event.Unable_all_area_recharge.post", NoParameters);
        vm.OnRobotState(State("FULL_CLEAN_FINISHED"));
        vm.OnRobotEvent("event.clean_finish.post", NoParameters);
        vm.OnRobotEvent("event.clean_record.post", Report(status: 4));

        Assert.Equal(["Zone inaccessible", "Nettoyage interrompu"], notifications.Select(n => n.Title));
    }

    [Fact]
    public void TheUnreachableAreaHasItsOwnSettingApartFromTheCleaningOnes()
    {
        using var vm = NewDashboard(new AppSettings { NotifyUnreachable = false }, out var notifications);
        vm.OnRobotEvent("event.Unable_all_area_recharge.post", NoParameters);
        Assert.Empty(notifications);

        vm.AppSettings.NotifyUnreachable = true;
        vm.AppSettings.TaskNotifications = TaskNotificationMode.None;
        vm.OnRobotEvent("event.Unable_all_area_recharge.post", NoParameters);
        Assert.Equal(["Zone inaccessible"], notifications.Select(n => n.Title));
    }

    [Fact]
    public void AStartNamesTheRoomsSentFromThisWindowJustBeforeAndOtherwiseAZoneOrNothing()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-07T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        using var vm = NewDashboard(new AppSettings { TaskNotifications = TaskNotificationMode.StartAndEnd }, out var notifications, clock);
        string StartBody(string mode = "zoneConfigured")
        {
            notifications.Clear();
            vm.OnRobotState(State("INACTIVE_CHARGING", "global"));
            vm.OnRobotState(State("FULL_CLEAN_RUNNING", mode));
            return Assert.Single(notifications).Body;
        }

        vm.Cleaning.LastLaunch = new(["Cuisine", "Couloir"], clock.GetUtcNow());
        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal("Le robot nettoie : Cuisine, Couloir.", StartBody());

        vm.Cleaning.LastLaunch = new(["Cuisine", "Couloir", "Salon", "Chambre", "Bureau"], clock.GetUtcNow());
        Assert.Equal("Le robot nettoie : Cuisine, Couloir, Salon et 2 autre(s).", StartBody());

        vm.Cleaning.LastLaunch = new([], clock.GetUtcNow());
        Assert.Equal("Le robot nettoie la zone.", StartBody("spotZoneConfigured"));

        // A launch long gone is not what this start is about: the clean came from the phone, a schedule or the robot.
        clock.Advance(TimeSpan.FromMinutes(3));
        Assert.Equal("Le robot nettoie une zone.", StartBody("spotZoneConfigured"));
        Assert.Equal("Le robot a commencé son nettoyage.", StartBody());
    }

    [Fact]
    public void ACleanAlreadyRunningWhenTheApplicationStartsIsNotToldAsStartedButItsEndIs()
    {
        using var vm = NewDashboard(new AppSettings { TaskNotifications = TaskNotificationMode.StartAndEnd }, out var notifications);

        // The first state seen is the application's starting point, whatever the robot is doing.
        vm.OnRobotState(State("FULL_CLEAN_RUNNING"));
        vm.OnRobotState(State("FULL_CLEAN_FINISHED"));
        vm.OnRobotEvent("event.clean_finish.post", NoParameters);
        vm.OnRobotEvent("event.clean_record.post", Report(status: 1, minutes: 70));

        var (title, body) = Assert.Single(notifications);
        Assert.Equal("Nettoyage terminé", title);
        Assert.Equal("Le robot a terminé son nettoyage en 1 h 10.", body);
    }

    [Fact]
    public void ACleanWithoutADurationIsToldWithoutOne()
    {
        using var vm = NewDashboard(new AppSettings(), out var notifications);

        PlayClean(vm, minutes: 0);

        Assert.Equal("Le robot a terminé son nettoyage.", Assert.Single(notifications).Body);
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
