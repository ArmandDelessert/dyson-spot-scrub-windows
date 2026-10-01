using System.Text.Json.Nodes;
using MyDyson.App.Rendering;
using MyDyson.App.ViewModels;
using MyDyson.Core;

namespace MyDyson.App.Tests;

public class RobotActivityTrackerTests
{
    private static RobotState State(string state, string dock = "IDLE", string action = "NONE") =>
        RobotState.Parse($$"""{"msg":"CURRENT-STATE","state":"{{state}}","dockState":"{{dock}}","fullCleanAction":"{{action}}"}""")!;

    /// <summary>Merges a jdm push into the tracker's running set of properties, as the session does.</summary>
    private static void Push(RobotActivityTracker t, JdmProperties jdm, string json)
    {
        jdm.Merge(JsonNode.Parse(json)!.AsObject());
        t.ApplyJdm(jdm);
    }

    [Fact]
    public void OnTheDockItChargesThenRests()
    {
        var t = new RobotActivityTracker();
        t.Apply(State("INACTIVE_CHARGING"));
        Assert.True(t.Docked);
        Assert.Equal(RobotActivity.Idle, t.Robot);
        Assert.Equal(DockActivity.Charging, t.Dock);

        t.Apply(State("INACTIVE_CHARGED"));
        Assert.True(t.Docked);
        Assert.Equal(DockActivity.Idle, t.Dock);
    }

    [Fact]
    public void AWashBeforeLeavingIsTheFillingAndOneAfterIsTheRollersWash()
    {
        var t = new RobotActivityTracker();
        var jdm = new JdmProperties();
        void Set(string json) => Push(t, jdm, json);

        // As captured on 20 September: the clean starts on the dock with a wash, then the robot leaves.
        Set("""{"charge_state":1,"station_act":0}""");
        t.Apply(State("FULL_CLEAN_RUNNING", action: "VACUUMING_AND_MOPPING"));
        Set("""{"station_act":1,"status":9}""");
        Assert.Equal(DockActivity.FillingWater, t.Dock);
        Assert.True(t.Docked);

        Set("""{"station_act":0,"status":6}""");
        Set("""{"charge_state":0}""");
        Assert.False(t.Docked);
        Assert.Equal(RobotActivity.VacuumingAndMopping, t.Robot);
        Assert.Equal(DockActivity.Idle, t.Dock);

        // Back to wash the roller mid-clean: driving, then washing on the dock.
        Set("""{"back_to_wash":1}""");
        Assert.Equal(RobotActivity.Moving, t.Robot);
        Set("""{"charge_state":1}""");
        Set("""{"station_act":1,"status":9,"back_to_wash":0}""");
        Assert.True(t.Docked);
        Assert.Equal(DockActivity.WashingRoller, t.Dock);
    }

    [Fact]
    public void TheDockEmptiesTheBinAndDriesTheRoller()
    {
        var t = new RobotActivityTracker();
        t.Apply(State("INACTIVE_CHARGING"));
        var jdm = new JdmProperties();
        Push(t, jdm, """{"station_act":5,"dust_action":1}""");
        Assert.Equal(DockActivity.EmptyingBin, t.Dock);
        Push(t, jdm, """{"station_act":2}""");
        Assert.Equal(DockActivity.DryingRoller, t.Dock);
        Push(t, jdm, """{"station_act":0}""");

        // Without jdm, the classic dockState says the same.
        var classic = new RobotActivityTracker();
        classic.Apply(State("INACTIVE_CHARGING", dock: "DRYING_MOP"));
        Assert.Equal(DockActivity.DryingRoller, classic.Dock);
        Assert.True(classic.Docked);
    }

    [Fact]
    public void OnTheFloorItCleansOrOnlyDrives()
    {
        var t = new RobotActivityTracker();
        t.Apply(State("FULL_CLEAN_RUNNING", action: "VACUUMING"));
        Assert.False(t.Docked);
        Assert.Equal(RobotActivity.Vacuuming, t.Robot);

        // The trail's last point: 0 while driving between rooms, 1 while cleaning.
        t.ApplyTrail([new RobotPosition(1, 0, 0, 0, 0)]);
        Assert.Equal(RobotActivity.Moving, t.Robot);
        t.ApplyTrail([new RobotPosition(2, 0, 0, 0, 1)]);
        Assert.Equal(RobotActivity.Vacuuming, t.Robot);

        t.Apply(State("FULL_CLEAN_PAUSED", action: "VACUUMING"));
        Assert.Equal(RobotActivity.Idle, t.Robot);
        t.Apply(State("FULL_CLEAN_DISCOVERING"));
        Assert.Equal(RobotActivity.Moving, t.Robot);
        t.Apply(State("INACTIVE_DISCHARGING"));
        Assert.Equal(RobotActivity.Idle, t.Robot);
        Assert.False(t.Docked);
    }
}
