using System.Text.Json.Nodes;
using DyssCockpit.Core;

namespace DyssCockpit.Core.Tests;

/// <summary>
/// The detector of a cleaning task's start and end, fed the sequences of states and events the robot
/// really pushed (docs/protocole.md, from the captures), each reduced to what the detector looks at.
/// </summary>
public class CleaningTaskDetectorTests
{
    /// <summary>Plays states and events into a detector and keeps what it reported, in order.</summary>
    private sealed class Replay
    {
        private readonly CleaningTaskDetector _detector = new();
        private long _reports;

        public List<CleaningTaskNotice> Notices { get; } = [];

        public Replay State(string state, string mode = "zoneConfigured")
        {
            Keep(_detector.OnState(RobotState.Parse($$$"""{"msg":"CURRENT-STATE","state":"{{{state}}}","currentCleaningMode":"{{{mode}}}"}""")!));
            return this;
        }

        public Replay Event(string name)
        {
            Keep(_detector.OnEvent(name, JsonNode.Parse($$$"""{"method":"{{{name}}}","params":{}}""")!.AsObject()));
            return this;
        }

        /// <summary>The robot's report of the task; <paramref name="startedAt"/> distinguishes one task from the next.</summary>
        public Replay Report(int status, int mode = 0, int minutes = 0, long? startedAt = null)
        {
            var start = startedAt ?? ++_reports;
            Keep(_detector.OnEvent("event.clean_record.post", JsonNode.Parse(
                $$$"""{"method":"event.clean_record.post","params":{"record_start_time":{{{start}}},"record_task_status":{{{status}}},"record_clean_mode":{{{mode}}},"record_use_time":{{{minutes}}}}}""")!.AsObject()));
            return this;
        }

        public Replay Reset()
        {
            _detector.Reset();
            return this;
        }

        public Replay Idle() => State("INACTIVE_CHARGING", "global");

        private void Keep(CleaningTaskNotice? notice)
        {
            if (notice is not null) Notices.Add(notice);
        }
    }

    private static CleaningTaskChange[] Changes(Replay replay) => [.. replay.Notices.Select(n => n.Change)];

    [Fact]
    public void ACleanFromStartToFinishIsToldOnceAtEachEnd()
    {
        // 2026-09-19 14:02, the robot waiting, then cleaning rooms; the finish comes a few minutes after FULL_CLEAN_FINISHED.
        var replay = new Replay()
            .Idle()
            .Event("event.startClean.post").State("FULL_CLEAN_RUNNING").State("FULL_CLEAN_DISCOVERING").State("FULL_CLEAN_RUNNING")
            .State("FULL_CLEAN_FINISHED").State("FULL_CLEAN_FINISHED").State("FULL_CLEAN_FINISHED")
            .Event("event.clean_finish.post").Report(status: 1, minutes: 8)
            .State("INACTIVE_CHARGING", "global");

        Assert.Equal([CleaningTaskChange.Started, CleaningTaskChange.Finished], Changes(replay));
        Assert.Equal(8, replay.Notices[1].Minutes);
        Assert.Equal("zoneConfigured", replay.Notices[0].CleaningMode);
        Assert.False(replay.Notices[0].IsZone);
    }

    [Fact]
    public void WashingTheRollerHalfWayStartsNothingAgain()
    {
        // 2026-09-20 19:11, a long clean: the state stays FULL_CLEAN_RUNNING through each wash and relocalisation.
        var replay = new Replay().Idle().Event("event.startClean.post")
            .State("FULL_CLEAN_RUNNING").State("FULL_CLEAN_RUNNING").State("FULL_CLEAN_RUNNING");
        foreach (var _ in new[] { 1, 2, 3 })
            replay.State("FULL_CLEAN_RUNNING").State("FULL_CLEAN_DISCOVERING").State("FULL_CLEAN_RUNNING");
        replay.State("FULL_CLEAN_FINISHED").Event("event.clean_finish.post").Report(status: 1, minutes: 101);

        Assert.Equal([CleaningTaskChange.Started, CleaningTaskChange.Finished], Changes(replay));
        Assert.Equal(101, replay.Notices[1].Minutes);
    }

    [Fact]
    public void RechargingHalfWayAndSettingOffAgainIsNotAStart()
    {
        var replay = new Replay().Idle().State("FULL_CLEAN_RUNNING").State("FULL_CLEAN_CHARGING").State("FULL_CLEAN_RUNNING");

        Assert.Equal([CleaningTaskChange.Started], Changes(replay));
    }

    [Fact]
    public void ResumingAfterAPauseIsNotAStart()
    {
        // The captures only have pauses that end in an abort; the resume is the same two states the other way round.
        var replay = new Replay().Idle().State("FULL_CLEAN_RUNNING").State("FULL_CLEAN_PAUSED").State("FULL_CLEAN_PAUSED").State("FULL_CLEAN_RUNNING");

        Assert.Equal([CleaningTaskChange.Started], Changes(replay));
    }

    [Fact]
    public void ACleanTheUserStopsIsStartedButNeverEnded()
    {
        // 2026-09-20 20:33: paused, then aborted from the app; the report says 2 and the state falls to ABORTED.
        var replay = new Replay().Idle().Event("event.startClean.post")
            .State("FULL_CLEAN_RUNNING").State("FULL_CLEAN_DISCOVERING").State("FULL_CLEAN_RUNNING").State("FULL_CLEAN_PAUSED")
            .Report(status: 2).State("ABORTED", "global").State("INACTIVE_DISCHARGING", "global").State("INACTIVE_CHARGING", "global");

        Assert.Equal([CleaningTaskChange.Started], Changes(replay));
    }

    [Fact]
    public void AStartThatIsStoppedAtOnceAndMadeAgainIsOneStart()
    {
        // 2026-09-20 18:17: startClean, a report of status 2 straight away, startClean again, and only then does the robot set off.
        var replay = new Replay().Idle()
            .Event("event.startClean.post").Report(status: 2)
            .Event("event.startClean.post").State("FULL_CLEAN_RUNNING").State("FULL_CLEAN_DISCOVERING");

        Assert.Equal([CleaningTaskChange.Started], Changes(replay));
    }

    [Fact]
    public void ARobotThatCannotLocateItselfGivesUpWithoutAnyFinish()
    {
        // 2026-09-19 14:00: locate_fail, then a report of status 4, and no clean_finish at all.
        var replay = new Replay().Idle().Event("event.startClean.post")
            .State("FULL_CLEAN_RUNNING").State("FULL_CLEAN_DISCOVERING")
            .Event("event.locate_fail.post").State("FULL_CLEAN_RUNNING").Report(status: 4)
            .State("INACTIVE_DISCHARGING", "global");

        Assert.Equal([CleaningTaskChange.Started, CleaningTaskChange.Abandoned], Changes(replay));
    }

    [Fact]
    public void AnUnreachableRoomEndsWithAFinishButTheCleanIsAbandonedNotFinished()
    {
        // 2026-09-20 15:20: Unable_all_area_recharge, FULL_CLEAN_FINISHED, clean_finish, and a report of status 4 with no minute and no area.
        var replay = new Replay().Idle().Event("event.startClean.post")
            .State("FULL_CLEAN_RUNNING").State("FULL_CLEAN_DISCOVERING").State("FULL_CLEAN_RUNNING")
            .Event("event.Unable_all_area_recharge.post").State("FULL_CLEAN_FINISHED")
            .Event("event.clean_finish.post").Report(status: 4)
            .State("INACTIVE_CHARGING", "global");

        Assert.Equal([CleaningTaskChange.Started, CleaningTaskChange.Abandoned], Changes(replay));
    }

    [Fact]
    public void ACleanAlreadyUnderWayWhenTheApplicationStartsIsNotAStartButIsEnded()
    {
        // 2026-09-20 16:48: the capture begins in the middle of a clean.
        var replay = new Replay()
            .State("FULL_CLEAN_RUNNING").State("FULL_CLEAN_RUNNING").State("FULL_CLEAN_FINISHED")
            .Event("event.clean_finish.post").Report(status: 1, minutes: 17);

        Assert.Equal([CleaningTaskChange.Finished], Changes(replay));
        Assert.Equal(17, replay.Notices[0].Minutes);
    }

    [Fact]
    public void ALostConnectionMakesTheNextStateAStartingPointAgain()
    {
        // The laptop wakes an hour after the clean began: it is not told as starting now.
        var replay = new Replay().Idle().State("FULL_CLEAN_RUNNING")
            .Reset().State("FULL_CLEAN_RUNNING").State("FULL_CLEAN_PAUSED").State("FULL_CLEAN_RUNNING");
        Assert.Equal([CleaningTaskChange.Started], Changes(replay));

        // ...whereas a clean that begins after that starting point is told.
        replay.Reset().Idle().State("FULL_CLEAN_RUNNING");
        Assert.Equal([CleaningTaskChange.Started, CleaningTaskChange.Started], Changes(replay));
    }

    [Fact]
    public void AMappingIsNeitherAStartNorAnEnd()
    {
        // 2026-09-20 20:35: MAPPING_RUNNING, MAPPING_FINISHED, then a report of status 1 in mode 4 and BuildMapFinish.
        var replay = new Replay().State("INACTIVE_DISCHARGING", "global")
            .Event("event.startBuildMap.post").State("MAPPING_RUNNING").State("MAPPING_FINISHED")
            .Report(status: 1, mode: 4, minutes: 1).Event("event.BuildMapFinish.post")
            .State("INACTIVE_DISCHARGING", "global");

        Assert.Empty(replay.Notices);
    }

    [Fact]
    public void AMappingTheRobotGivesUpIsNotACleanGivenUp()
    {
        var replay = new Replay().State("INACTIVE_DISCHARGING", "global")
            .Event("event.startBuildMap.post").State("MAPPING_RUNNING")
            .Report(status: 4, mode: 4);

        Assert.Empty(replay.Notices);
    }

    [Fact]
    public void AZoneCleanedIsStartedAsAZoneAndEndedLikeAnyClean()
    {
        // 2026-09-30 18:50: spotZoneConfigured, ended by clean_finish and a report of mode 4, as a mapping's is.
        var replay = new Replay().Idle().Event("event.startClean.post")
            .State("FULL_CLEAN_RUNNING", "spotZoneConfigured").State("FULL_CLEAN_DISCOVERING", "spotZoneConfigured").State("FULL_CLEAN_RUNNING", "spotZoneConfigured")
            .State("FULL_CLEAN_FINISHED", "spotZoneConfigured").Event("event.clean_finish.post").Report(status: 1, mode: 4, minutes: 3);

        Assert.Equal([CleaningTaskChange.Started, CleaningTaskChange.Finished], Changes(replay));
        Assert.True(replay.Notices[0].IsZone);
        Assert.Equal(3, replay.Notices[1].Minutes);
    }

    [Fact]
    public void AReportOfMode4WithNoFinishIsNotACleanDone()
    {
        // What a mapping looks like to a detector that never saw it begin (the connection came back in the middle of it).
        var replay = new Replay().Idle().Report(status: 1, mode: 4, minutes: 1);

        Assert.Empty(replay.Notices);
    }

    [Fact]
    public void ARoomsCleanWhoseFinishWasLostIsStillTold()
    {
        var replay = new Replay().Idle().State("FULL_CLEAN_RUNNING").State("FULL_CLEAN_FINISHED").Report(status: 1, mode: 0, minutes: 12);

        Assert.Equal([CleaningTaskChange.Started, CleaningTaskChange.Finished], Changes(replay));
    }

    [Fact]
    public void WhatArrivesTwiceIsToldOnce()
    {
        var replay = new Replay().Idle()
            .State("FULL_CLEAN_RUNNING").State("FULL_CLEAN_RUNNING")
            .Event("event.clean_finish.post").Event("event.clean_finish.post")
            .Report(status: 1, minutes: 5, startedAt: 1789819321).Report(status: 1, minutes: 5, startedAt: 1789819321);

        Assert.Equal([CleaningTaskChange.Started, CleaningTaskChange.Finished], Changes(replay));
    }

    [Fact]
    public void ASecondCleanIsToldAfterTheFirst()
    {
        var replay = new Replay().Idle()
            .State("FULL_CLEAN_RUNNING").State("FULL_CLEAN_FINISHED").Event("event.clean_finish.post").Report(status: 1, minutes: 5)
            .State("INACTIVE_CHARGING", "global")
            .State("FULL_CLEAN_RUNNING").State("FULL_CLEAN_FINISHED").Event("event.clean_finish.post").Report(status: 1, minutes: 7);

        Assert.Equal(
            [CleaningTaskChange.Started, CleaningTaskChange.Finished, CleaningTaskChange.Started, CleaningTaskChange.Finished],
            Changes(replay));
        Assert.Equal([5, 7], replay.Notices.Where(n => n.Change == CleaningTaskChange.Finished).Select(n => n.Minutes));
    }

    [Fact]
    public void AMessageWithoutAStateChangesNothing()
    {
        // A CURRENT-STATE that carries no state (a position, a setting): the one before still holds.
        var detector = new CleaningTaskDetector();
        Assert.Null(detector.OnState(RobotState.Parse("""{"msg":"CURRENT-STATE","state":"INACTIVE_CHARGING"}""")!));
        Assert.Null(detector.OnState(RobotState.Parse("""{"msg":"CURRENT-STATE","batteryChargeLevel":50}""")!));

        Assert.Equal(CleaningTaskChange.Started, detector.OnState(RobotState.Parse("""{"msg":"CURRENT-STATE","state":"FULL_CLEAN_RUNNING"}""")!)?.Change);
    }

    [Fact]
    public void AReportWithoutDetailsIsReadWithoutFailing()
    {
        var detector = new CleaningTaskDetector();

        Assert.Null(detector.OnEvent("event.clean_record.post", JsonNode.Parse("""{"method":"event.clean_record.post"}""")!.AsObject()));
        Assert.Null(detector.OnEvent("event.clean_record.post", JsonNode.Parse("""{"params":{"record_task_status":"1"}}""")!.AsObject()));
        Assert.Null(detector.OnEvent("event.map_change.post", new JsonObject()));
    }
}
