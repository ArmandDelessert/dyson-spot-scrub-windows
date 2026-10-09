using DyssCockpit.Core;

namespace DyssCockpit.Core.Tests;

/// <summary>
/// The watcher of faults the robot starts to report, fed states shaped like the captures': a
/// 21xx indicator is there all the time, the faults that need someone come and go.
/// </summary>
public class RobotFaultWatcherTests
{
    private static RobotState State(string state, params (string Code, string Action)[] faults) =>
        RobotState.Parse($$$"""{"msg":"CURRENT-STATE","state":"{{{state}}}","activeFaults":[{{{string.Join(",", faults.Select(f => $$"""{"faultCode":"{{f.Code}}","nextActionRequired":"{{f.Action}}"}"""))}}}]}""")!;

    private static string[] Codes(IReadOnlyList<ActiveFault> faults) => [.. faults.Select(f => f.FaultCode)];

    [Fact]
    public void ALocateFailureIsToldOnceWhateverNumberOfStatesRepeatIt()
    {
        // 2026-09-19 14:01: the robot localises (indicator 2108), fails, 589 stays until it is cleared.
        var watcher = new Script();
        Assert.Empty(watcher.Feed(State("INACTIVE_CHARGING", ("2105", "LOG_ONLY"))));
        Assert.Empty(watcher.Feed(State("FULL_CLEAN_DISCOVERING", ("2108", "LOG_ONLY"))));

        Assert.Equal(["589"], watcher.Feed(State("FULL_CLEAN_RUNNING", ("589", "WAIT_TO_CLEAR"))));
        Assert.Empty(watcher.Feed(State("FULL_CLEAN_RUNNING", ("589", "WAIT_TO_CLEAR"))));
        Assert.Empty(watcher.Feed(State("INACTIVE_DISCHARGING", ("589", "WAIT_TO_CLEAR"))));
    }

    [Fact]
    public void ThePassingFaultOfTheWayBackToTheDockIsNotTold()
    {
        var watcher = new Script();
        watcher.Feed(State("FULL_CLEAN_RUNNING", ("2109", "LOG_ONLY")));
        watcher.Feed(State("FULL_CLEAN_RUNNING", ("589", "WAIT_TO_CLEAR")));

        Assert.Empty(watcher.Feed(State("INACTIVE_DISCHARGING", ("501", "WAIT_TO_CLEAR"), ("589", "WAIT_TO_CLEAR"))));
        Assert.Empty(watcher.Feed(State("INACTIVE_DISCHARGING", ("501", "WAIT_TO_CLEAR"))));
    }

    [Fact]
    public void StatusIndicatorsAreNeverTold()
    {
        var watcher = new Script();
        watcher.Feed(State("INACTIVE_CHARGING"));

        Assert.Empty(watcher.Feed(State("FULL_CLEAN_RUNNING", ("2109", "LOG_ONLY"))));
        Assert.Empty(watcher.Feed(State("FULL_CLEAN_FINISHED", ("2102", "LOG_ONLY"))));
        Assert.Empty(watcher.Feed(State("INACTIVE_CHARGING", ("2103", "LOG_ONLY"))));
    }

    [Fact]
    public void AFaultThatGoesAndComesBackIsToldAgain()
    {
        // 2026-09-20 15:26: an unreachable room leaves 2007 on until the robot is charging again.
        var watcher = new Script();
        watcher.Feed(State("FULL_CLEAN_RUNNING", ("2109", "LOG_ONLY")));

        Assert.Equal(["2007"], watcher.Feed(State("FULL_CLEAN_FINISHED", ("2007", "USER_CONTINUE"))));
        Assert.Empty(watcher.Feed(State("INACTIVE_CHARGING", ("2007", "USER_CONTINUE"))));
        Assert.Empty(watcher.Feed(State("INACTIVE_CHARGING", ("2105", "LOG_ONLY"))));
        Assert.Equal(["2007"], watcher.Feed(State("FULL_CLEAN_FINISHED", ("2007", "USER_CONTINUE"))));
    }

    [Fact]
    public void OnlyTheNewOneOfTwoFaultsIsTold()
    {
        var watcher = new Script();
        watcher.Feed(State("FULL_CLEAN_RUNNING", ("2007", "USER_CONTINUE")));

        Assert.Equal(["589"], watcher.Feed(State("FULL_CLEAN_RUNNING", ("2007", "USER_CONTINUE"), ("589", "WAIT_TO_CLEAR"))));
    }

    [Fact]
    public void AFaultAlreadyThereAtTheStartIsNotNewsAndNeitherIsOneAfterALostConnection()
    {
        var watcher = new Script();

        Assert.Empty(watcher.Feed(State("INACTIVE_DISCHARGING", ("589", "WAIT_TO_CLEAR"))));   // the application has just started
        Assert.Empty(watcher.Feed(State("INACTIVE_DISCHARGING", ("589", "WAIT_TO_CLEAR"))));

        watcher.Reset();   // the link dropped, and came back
        Assert.Empty(watcher.Feed(State("INACTIVE_DISCHARGING", ("589", "WAIT_TO_CLEAR"), ("2007", "USER_CONTINUE"))));
        Assert.Equal(["5000"], watcher.Feed(State("INACTIVE_DISCHARGING", ("589", "WAIT_TO_CLEAR"), ("2007", "USER_CONTINUE"), ("5000", "WAIT_TO_CLEAR"))));
    }

    [Fact]
    public void AMessageWithNoStateLeavesWhatWasKnownAlone()
    {
        var watcher = new Script();
        watcher.Feed(State("FULL_CLEAN_RUNNING", ("589", "WAIT_TO_CLEAR")));

        // A position alone, or any message that carries no state, says nothing about the faults.
        Assert.Empty(watcher.Feed(RobotState.Parse("""{"msg":"CURRENT-STATE","batteryChargeLevel":50}""")!));
        Assert.Empty(watcher.Feed(State("FULL_CLEAN_RUNNING", ("589", "WAIT_TO_CLEAR"))));
    }

    /// <summary>A watcher fed one state after another, answering with the codes it reports.</summary>
    private sealed class Script
    {
        private readonly RobotFaultWatcher _watcher = new();

        public string[] Feed(RobotState state) => Codes(_watcher.OnState(state));

        public void Reset() => _watcher.Reset();
    }
}
