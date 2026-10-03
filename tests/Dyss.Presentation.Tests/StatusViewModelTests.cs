using Dyss.Presentation.ViewModels;
using Dyss.Core;

namespace Dyss.Presentation.Tests;

/// <summary>
/// What the state card shows. Everything here is pure: a robot message in, the text and flags the
/// XAML binds to out.
/// </summary>
public class StatusViewModelTests
{
    private static StatusViewModel New() => new(TestHub.Create());

    private static RobotState State(string json) => RobotState.Parse(json)!;

    private static void ApplyJdm(StatusViewModel vm, string props)
    {
        var jdm = new JdmProperties();
        jdm.Merge((System.Text.Json.Nodes.JsonObject)System.Text.Json.Nodes.JsonNode.Parse(props)!);
        vm.ApplyJdm(jdm);
    }

    [Fact]
    public void AKnownStateBecomesFrenchAndAnUnknownOneIsShownAsIs()
    {
        var vm = New();

        vm.Apply(State("""{"msg":"CURRENT-STATE","state":"INACTIVE_CHARGING"}"""));
        Assert.Equal("En charge sur la station", vm.StateText);

        // A state a future firmware invents must still tell the user something.
        vm.Apply(State("""{"msg":"CURRENT-STATE","state":"SOMETHING_NEW"}"""));
        Assert.Equal("SOMETHING_NEW", vm.StateText);
    }

    [Fact]
    public void PauseAndAbortFollowWhatTheRobotIsDoing()
    {
        var vm = New();

        vm.Apply(State("""{"msg":"CURRENT-STATE","state":"INACTIVE_CHARGING"}"""));
        Assert.False(vm.CanPause);
        Assert.False(vm.CanAbort);

        vm.Apply(State("""{"msg":"CURRENT-STATE","state":"FULL_CLEAN_RUNNING"}"""));
        Assert.True(vm.CanPause);
        Assert.True(vm.CanAbort);
        Assert.Equal("⏸ Pause", vm.PauseResumeLabel);

        // Still "can pause": the one button toggles, and only the label says which way.
        vm.Apply(State("""{"msg":"CURRENT-STATE","state":"FULL_CLEAN_PAUSED"}"""));
        Assert.True(vm.CanPause);
        Assert.Equal("▶ Reprendre", vm.PauseResumeLabel);
    }

    [Fact]
    public void OnlyRealFaultsAreShownInRed()
    {
        var vm = New();

        // The 21xx family with LOG_ONLY is a status indicator, not a breakdown.
        vm.Apply(State("""{"msg":"CURRENT-STATE","state":"INACTIVE_CHARGING","activeFaults":[{"faultCode":"2105","nextActionRequired":"LOG_ONLY"}]}"""));
        Assert.False(vm.HasRealFault);
        Assert.Equal("", vm.FaultText);

        vm.Apply(State("""{"msg":"CURRENT-STATE","state":"FULL_CLEAN_RUNNING","activeFaults":[{"faultCode":"589","nextActionRequired":"WAIT_TO_CLEAR"}]}"""));
        Assert.True(vm.HasRealFault);
        Assert.Equal("faute 589 (localisation impossible)", vm.FaultText);
    }

    [Fact]
    public void TheDockLineNamesTheActionAndTheWashDryButtonOffersToStopIt()
    {
        var vm = New();

        vm.Apply(State("""{"msg":"CURRENT-STATE","state":"INACTIVE_CHARGING","dockState":"IDLE"}"""));
        Assert.Equal("", vm.DockText);
        Assert.False(vm.DockBusy);
        Assert.Equal("Laver et sécher", vm.WashDryLabel);

        vm.Apply(State("""{"msg":"CURRENT-STATE","state":"INACTIVE_CHARGING","dockState":"WASHING_MOP"}"""));
        Assert.Equal("Station : lavage du rouleau humide", vm.DockText);
        Assert.True(vm.DockBusy);
        Assert.Equal("Arrêter le lavage", vm.WashDryLabel);
    }

    [Fact]
    public void TheDryingCountdownIsAppendedWhileTheDockIsDrying()
    {
        var vm = New();
        vm.Apply(State("""{"msg":"CURRENT-STATE","state":"INACTIVE_CHARGING","dockState":"DRYING_MOP"}"""));

        // station_act 2 is drying; work_time counts down in 60 s steps from the 3 h setting.
        ApplyJdm(vm, """{"station_act":2,"work_time":{"type":3,"total":10800,"surplus":10740}}""");
        Assert.Equal("Station : séchage du rouleau humide, 2 h 59 min restantes", vm.DockText);

        // Under an hour it reads in minutes, rounded up so it never shows "0 min" while running.
        ApplyJdm(vm, """{"station_act":2,"work_time":{"type":3,"total":10800,"surplus":90}}""");
        Assert.Equal("Station : séchage du rouleau humide, 2 min restantes", vm.DockText);
        ApplyJdm(vm, """{"station_act":2,"work_time":{"type":3,"total":10800,"surplus":1}}""");
        Assert.Equal("Station : séchage du rouleau humide, 1 min restantes", vm.DockText);
    }

    [Fact]
    public void AStaleCountdownIsNotShownOnceTheDockIsDoneDrying()
    {
        var vm = New();
        vm.Apply(State("""{"msg":"CURRENT-STATE","state":"INACTIVE_CHARGING","dockState":"DRYING_MOP"}"""));
        ApplyJdm(vm, """{"station_act":2,"work_time":{"type":3,"total":10800,"surplus":600}}""");
        Assert.EndsWith("10 min restantes", vm.DockText, StringComparison.Ordinal);

        // work_time lingers at its last value after drying ends; station_act is what says it stopped.
        ApplyJdm(vm, """{"station_act":0,"work_time":{"type":3,"total":10800,"surplus":600}}""");
        vm.Apply(State("""{"msg":"CURRENT-STATE","state":"INACTIVE_CHARGING","dockState":"IDLE"}"""));
        Assert.Equal("", vm.DockText);
    }

    [Fact]
    public void TheTripBackToWashIsShownBecauseTheClassicDialectDoesNotReportIt()
    {
        var vm = New();
        Assert.False(vm.ReturningToWash);

        ApplyJdm(vm, """{"back_to_wash":1}""");
        Assert.True(vm.ReturningToWash);

        // Back to 0 once docked, when station_act 1 takes over and the dock line says "lavage".
        ApplyJdm(vm, """{"station_act":1,"back_to_wash":0}""");
        Assert.False(vm.ReturningToWash);
    }

    [Fact]
    public void ConsumableRowsAreReplacedOnlyWhenTheirValuesChange()
    {
        // State messages arrive several times a minute while cleaning; rebuilding every row each
        // time re-created its visuals for nothing.
        var vm = New();
        const string Two = """{"msg":"CURRENT-STATE","consumables":[{"type":"brushBar","usage":18},{"type":"cleaningSolution","needsRefill":false}]}""";
        vm.Apply(State(Two));
        var first = vm.Consumables.ToArray();
        Assert.Equal(["Brosse", "Produit de nettoyage"], vm.Consumables.Select(c => c.Name));
        Assert.Equal(82, vm.Consumables[0].Remaining);

        vm.Apply(State(Two));
        Assert.Same(first[0], vm.Consumables[0]);
        Assert.Same(first[1], vm.Consumables[1]);

        vm.Apply(State("""{"msg":"CURRENT-STATE","consumables":[{"type":"brushBar","usage":19},{"type":"cleaningSolution","needsRefill":false}]}"""));
        Assert.NotSame(first[0], vm.Consumables[0]);
        Assert.Same(first[1], vm.Consumables[1]);
        Assert.Equal(81, vm.Consumables[0].Remaining);
    }

    [Fact]
    public void AConsumableWithoutAUsageReadsAsReadyRatherThanEmpty()
    {
        var vm = New();
        vm.Apply(State("""{"msg":"CURRENT-STATE","consumables":[{"type":"cleaningSolution","needsRefill":true}]}"""));

        var solution = Assert.Single(vm.Consumables);
        Assert.Equal("à recharger", solution.Display);
        Assert.Equal(100, solution.Remaining); // no usage reported: nothing to draw as worn
    }

    [Fact]
    public void APositionOnlyMessageDoesNotBlankTheCard()
    {
        // The robot pushes bare position updates constantly while cleaning; they carry no state.
        var vm = New();
        vm.Apply(State("""{"msg":"CURRENT-STATE","state":"FULL_CLEAN_RUNNING","batteryChargeLevel":80,"fullCleanAction":"VACUUMING"}"""));
        var before = (vm.StateText, vm.ActionText, vm.Battery);

        var position = State("""{"msg":"CURRENT-STATE","globalPosition":[{"id":613,"x":0.9,"y":2.2,"angle":-2.6,"update":1}]}""");
        Assert.True(position.IsPositionOnly);
        vm.Apply(State("""{"msg":"CURRENT-STATE","state":"FULL_CLEAN_RUNNING","batteryChargeLevel":80,"fullCleanAction":"VACUUMING"}""").Merge(position));

        Assert.Equal(before, (vm.StateText, vm.ActionText, vm.Battery));
        Assert.Equal("aspiration", vm.ActionText);
    }
}
