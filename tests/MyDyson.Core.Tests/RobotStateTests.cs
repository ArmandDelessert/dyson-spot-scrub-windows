using System.Text.Json.Nodes;
using MyDyson.Core;

namespace MyDyson.Core.Tests;

public class RobotStateTests
{
    // Anonymised CURRENT-STATE captured from an RB05 on 2026-09-19.
    private const string FullState = """
        {"msg":"CURRENT-STATE","time":"2026-09-19T11:53:11.9534615Z","batteryChargeLevel":100,
         "state":"INACTIVE_CHARGING","currentCleaningStrategy":"auto","defaultCleaningStrategy":"auto",
         "currentCleaningMode":"global","defaultCleaningMode":"zoneConfigured","cleanDuration":480,
         "persistentMapId":"1000000002",
         "activeFaults":[{"faultCode":"2105","nextActionRequired":"LOG_ONLY"}],
         "outOfBoxState":"OUT_OF_BOX_COMPLETE","hotWaterMop":true,"backWashType":"TIME","hotWaterSwitch":true,
         "detergent":true,"volume":30,"fullCleanAction":"NONE","dockState":"IDLE","airDryFrequency":3,
         "backWashTime":15,"backWashFrequency":20,"alarm":true,"voiceLanguage":"fr-FR",
         "consumables":[{"type":"brushBar","usage":18},{"type":"cleaningSolution","needsRefill":false}],
         "collectDustOnSelfClean":true,"childLock":false,
         "doNotDisturbMode":{"isOn":false,"startTime":"22:00","endTime":"8:00"}}
        """;

    private const string PositionOnly = """
        {"msg":"CURRENT-STATE","globalPosition":[{"id":613,"x":0.937234,"y":2.206837,"angle":-2.622639,"update":1}]}
        """;

    [Fact]
    public void ParsesTheFullMessage()
    {
        var s = RobotState.Parse(FullState)!;

        Assert.Equal("INACTIVE_CHARGING", s.State);
        Assert.Equal(100, s.BatteryChargeLevel);
        Assert.Equal("IDLE", s.DockState);
        Assert.True(s.IsDocked);
        Assert.False(s.IsCleaning);
        Assert.False(s.IsDockBusy);
        Assert.Equal(480, s.CleanDurationSeconds);
        Assert.Equal(2, s.Consumables!.Count);
        Assert.Equal(18, s.Consumables[0].Usage);
        Assert.False(s.Consumables[1].NeedsRefill);
        Assert.Equal("22:00", s.DoNotDisturbMode!.StartTime);
        Assert.False(s.IsPositionOnly);
    }

    [Fact]
    public void StatusIndicatorFaultsAreNotRealFaults()
    {
        var s = RobotState.Parse(FullState)!;
        Assert.Single(s.ActiveFaults!);
        Assert.Empty(s.RealFaults);

        var stuck = RobotState.Parse("""{"msg":"CURRENT-STATE","state":"FULL_CLEAN_RUNNING","activeFaults":[{"faultCode":"589","nextActionRequired":"WAIT_TO_CLEAR"}]}""")!;
        var fault = Assert.Single(stuck.RealFaults);
        Assert.True(fault.NeedsUser);
    }

    [Fact]
    public void PositionOnlyUpdateKeepsEverythingElse()
    {
        var full = RobotState.Parse(FullState)!;
        var pos = RobotState.Parse(PositionOnly)!;
        Assert.True(pos.IsPositionOnly);

        var merged = full.Merge(pos);

        Assert.Equal("INACTIVE_CHARGING", merged.State);
        Assert.Equal(100, merged.BatteryChargeLevel);
        Assert.Equal(613, merged.LatestPosition!.Id);
        Assert.Equal(0.937234, merged.LatestPosition.X, 6);
    }

    [Fact]
    public void FullUpdateReplacesButKeepsLastPosition()
    {
        var withPos = RobotState.Parse(FullState)!.Merge(RobotState.Parse(PositionOnly)!);
        var newer = RobotState.Parse("""{"msg":"CURRENT-STATE","state":"FULL_CLEAN_RUNNING","batteryChargeLevel":97}""")!;

        var merged = withPos.Merge(newer);

        Assert.Equal("FULL_CLEAN_RUNNING", merged.State);
        Assert.Equal(97, merged.BatteryChargeLevel);
        Assert.Null(merged.DockState); // a full message is authoritative for what it omits
        Assert.Equal(613, merged.LatestPosition!.Id);
    }

    [Fact]
    public void TrackerFoldsBothDialects()
    {
        var tracker = new RobotStateTracker();
        var changes = 0;
        tracker.StateChanged += _ => changes++;

        tracker.Apply(new RobotMessage(DateTimeOffset.UtcNow, "RB05/S/status", FullState));
        tracker.Apply(new RobotMessage(DateTimeOffset.UtcNow, "RB05/S/status", PositionOnly));
        tracker.Apply(new RobotMessage(DateTimeOffset.UtcNow, "RB05/S/status/jdm",
            """{"msgId":"1","method":"prop.post","params":{"cleaning_area":756,"sweep_type":7}}"""));
        tracker.Apply(new RobotMessage(DateTimeOffset.UtcNow, "RB05/S/status/jdm",
            """{"msgId":"2","code":1,"method":"prop.get","data":{"quantity":98,"status":4}}"""));

        Assert.Equal(2, changes);
        Assert.Equal("INACTIVE_CHARGING", tracker.State!.State);
        Assert.Equal(613, tracker.State.LatestPosition!.Id);
        Assert.Equal(756, tracker.Jdm.CleaningArea);
        Assert.Equal(7, tracker.Jdm.SweepType);
        Assert.Equal(98, tracker.Jdm.Battery);
    }

    [Fact]
    public void CleanPathAccumulatesAcrossPushesAndResetsOnANewTask()
    {
        var tracker = new RobotStateTracker();
        var pushes = 0;
        tracker.CleanPathChanged += _ => pushes++;

        // One point, then a two-point batch, as actually observed on the wire.
        tracker.Apply(new RobotMessage(DateTimeOffset.UtcNow, "RB05/S/status/jdm",
            """{"msgId":"1","method":"prop.post","params":{"cur_path":[101,-0.76,3.23,-2.64,1,1789915754]}}"""));
        tracker.Apply(new RobotMessage(DateTimeOffset.UtcNow, "RB05/S/status/jdm",
            """{"msgId":"2","method":"prop.post","params":{"cur_path":[103,-0.77,3.22,-1.55,1,-0.76,3.11,-1.59,0,1789915756]}}"""));

        Assert.Equal(2, pushes);
        Assert.Equal(3, tracker.CleanPath.Count);
        Assert.Equal(1, tracker.CleanPath[0].Update);
        Assert.Equal(0, tracker.CleanPath[2].Update);
        Assert.Equal(-0.76, tracker.CleanPath[2].X, 2);

        // A lower leading id means a new task started: the old points are dropped.
        tracker.Apply(new RobotMessage(DateTimeOffset.UtcNow, "RB05/S/status/jdm",
            """{"msgId":"3","method":"prop.post","params":{"cur_path":[5,0,0,0,1,10]}}"""));
        Assert.Single(tracker.CleanPath);

        // event.startClean.post also clears it, even without an id rollback.
        tracker.Apply(new RobotMessage(DateTimeOffset.UtcNow, "RB05/S/status/jdm",
            """{"msgId":"4","method":"event.startClean.post","params":{}}"""));
        Assert.Empty(tracker.CleanPath);
    }

    [Fact]
    public void CleanPathHandsOutSnapshotsNotTheLiveList()
    {
        // The UI enumerates what it received later, on another thread, while the MQTT thread keeps
        // appending: what the event and the property hand out must not change under its feet.
        var tracker = new RobotStateTracker();
        IReadOnlyList<RobotPosition>? received = null;
        tracker.CleanPathChanged += p => received ??= p;   // keep the first snapshot only

        tracker.Apply(new RobotMessage(DateTimeOffset.UtcNow, "RB05/S/status/jdm",
            """{"msgId":"1","method":"prop.post","params":{"cur_path":[1,0,0,0,1,10]}}"""));
        var fromProperty = tracker.CleanPath;
        tracker.Apply(new RobotMessage(DateTimeOffset.UtcNow, "RB05/S/status/jdm",
            """{"msgId":"2","method":"prop.post","params":{"cur_path":[2,1,1,0,1,11]}}"""));

        Assert.Single(received!);
        Assert.Single(fromProperty);
        Assert.Equal(2, tracker.CleanPath.Count);
    }

    [Fact]
    public void MalformedCleanPathBatchesAreIgnoredWithoutLosingThePath()
    {
        var tracker = new RobotStateTracker();
        var pushes = 0;
        tracker.CleanPathChanged += _ => pushes++;
        tracker.Apply(new RobotMessage(DateTimeOffset.UtcNow, "RB05/S/status/jdm",
            """{"msgId":"1","method":"prop.post","params":{"cur_path":[7,0.1,0.2,0,1,10]}}"""));

        // A non-number, a length that is not 2 + 4n, and a batch too short to hold a point: none
        // of them touches the accumulated path, but the rest of the push is still merged.
        foreach (var bad in new[] { "[8,\"x\",0,0,1,11]", "[8,0,0,0,1,0,12]", "[8,13]", "[]", "{}", "5" })
            tracker.Apply(new RobotMessage(DateTimeOffset.UtcNow, "RB05/S/status/jdm",
                $$$"""{"msgId":"2","method":"prop.post","params":{"cur_path":{{{bad}}},"cleaning_area":42}}"""));

        Assert.Equal(1, pushes);
        var only = Assert.Single(tracker.CleanPath);
        Assert.Equal(7, only.Id);
        Assert.Equal(42, tracker.Jdm.CleaningArea);
    }

    [Fact]
    public void VoiceDownloadStatusIsTracked()
    {
        var tracker = new RobotStateTracker();
        tracker.Apply(new RobotMessage(DateTimeOffset.UtcNow, "RB05/S/status",
            """{"msg":"VOICE-DOWNLOAD-STATUS","state":"downloading","language":"fr-CA","progress":57}"""));

        Assert.Equal("downloading", tracker.VoiceDownload!.State);
        Assert.Equal(57, tracker.VoiceDownload.Progress);
        Assert.Null(tracker.State);
    }
}

public class RoomPreferenceTests
{
    [Fact]
    public void PlainNameAndTypedNameAreBothDecoded()
    {
        var rooms = RoomPreference.ParseAll(JsonNode.Parse("""
            [[11, "Pièce 1", 2, 0, 0, 0, 0, 0, 1, 0, 1, 0],
             [10, "{\"type\":\"kitchen\",\"name\":\"Pièce 2\"}", 3, 0, 0, 0, 0, 0, 0, 0, 2]]
            """));

        Assert.Equal(2, rooms.Count);
        Assert.Equal((11, "Pièce 1", (string?)null), (rooms[0].Id, rooms[0].Name, rooms[0].Type));
        Assert.Equal((10, "Pièce 2", "kitchen"), (rooms[1].Id, rooms[1].Name, rooms[1].Type));
        Assert.Equal(10, rooms[0].Rest.Count); // twelve elements
        Assert.Equal(9, rooms[1].Rest.Count);  // eleven elements, as published by the app
    }

    [Fact]
    public void JoinNameRoundTrips()
    {
        var encoded = RoomPreference.JoinName("Débarras", "storageRoom");
        Assert.Equal(("Débarras", "storageRoom"), RoomPreference.SplitName(encoded));
        Assert.Equal(("Salon", (string?)null), RoomPreference.SplitName(RoomPreference.JoinName("Salon", null)));
    }

    [Fact]
    public void MalformedEntriesAreSkipped()
    {
        var rooms = RoomPreference.ParseAll(JsonNode.Parse("""[[1], "x", [2, "ok"]]"""));
        var only = Assert.Single(rooms);
        Assert.Equal(2, only.Id);
    }
}
