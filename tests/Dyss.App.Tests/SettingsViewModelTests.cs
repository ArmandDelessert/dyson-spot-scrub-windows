using Dyss.App.ViewModels;
using Dyss.Core;

namespace Dyss.App.Tests;

/// <summary>
/// The Réglages tab. The tests lean on one fact: with no MQTT session, any attempt to send a
/// command sets the hub's message to "Robot non connecté" — so that message is a reliable witness
/// of whether a control tried to write to the robot.
/// </summary>
public class SettingsViewModelTests
{
    private const string NotConnected = "Robot non connecté.";

    private static SettingsViewModel New(out RobotHub hub)
    {
        hub = TestHub.Create();
        return new SettingsViewModel(hub);
    }

    private static RobotState State(string json) => RobotState.Parse(json)!;

    [Fact]
    public void ValuesComingFromTheRobotAreNotEchoedBackToIt()
    {
        // Otherwise every state message would re-send every setting, and a setting changed from the
        // phone would be fought over by the two apps.
        var vm = New(out var hub);

        vm.Apply(State("""{"msg":"CURRENT-STATE","hotWaterMop":true,"detergent":true,"alarm":true,"volume":30,"airDryFrequency":4}"""));

        Assert.True(vm.HotWaterMop);
        Assert.Equal(30, vm.Volume);
        Assert.Equal(4, vm.DryDuration!.Hours);
        Assert.Equal("", hub.Message);
    }

    [Fact]
    public void AUserChangeIsSentToTheRobot()
    {
        var vm = New(out var hub);
        vm.Apply(State("""{"msg":"CURRENT-STATE","hotWaterMop":false}"""));
        Assert.Equal("", hub.Message);

        vm.HotWaterMop = true;

        Assert.Equal(NotConnected, hub.Message);
    }

    [Theory]
    [InlineData("""{"msg":"CURRENT-STATE","backWashType":"ROOM"}""", "Après chaque pièce")]
    [InlineData("""{"msg":"CURRENT-STATE","backWashType":"TIME","backWashTime":15}""", "Toutes les 15 min")]
    [InlineData("""{"msg":"CURRENT-STATE","backWashType":"TIME","backWashTime":30}""", "Toutes les 30 min")]
    [InlineData("""{"msg":"CURRENT-STATE","backWashType":"ONLY_WHEN_NEEDED"}""", "Uniquement si nécessaire")]
    public void TheSelfCleanIntervalCombinesTypeAndMinutes(string json, string label)
    {
        var vm = New(out _);

        vm.Apply(State(json));

        Assert.Equal(label, vm.BackWash!.Label);
    }

    [Fact]
    public void AnUnknownIntervalLeavesTheCurrentChoiceAlone()
    {
        var vm = New(out _);
        vm.Apply(State("""{"msg":"CURRENT-STATE","backWashType":"ROOM"}"""));

        vm.Apply(State("""{"msg":"CURRENT-STATE","backWashType":"SOMETHING_NEW"}"""));

        Assert.Equal("Après chaque pièce", vm.BackWash!.Label);
    }

    [Fact]
    public void TheSolutionLineFollowsTheRefillFlag()
    {
        var vm = New(out _);

        vm.Apply(State("""{"msg":"CURRENT-STATE","consumables":[{"type":"cleaningSolution","needsRefill":false}]}"""));
        Assert.Equal("Prêt à l'emploi", vm.SolutionStatus);

        vm.Apply(State("""{"msg":"CURRENT-STATE","consumables":[{"type":"cleaningSolution","needsRefill":true}]}"""));
        Assert.Equal("À recharger", vm.SolutionStatus);
    }

    [Fact]
    public void TheWashBeforeCleanCheckboxIsMarkedUnknownUntilTheRobotReportsIt()
    {
        // The robot never pushes this one back, so the UI says the box only reflects the last
        // choice made here; the flag is what drives that note.
        var vm = New(out _);
        Assert.False(vm.WashMopBeforeCleanKnown);

        vm.Apply(State("""{"msg":"CURRENT-STATE","washMopBeforeClean":true}"""));

        Assert.True(vm.WashMopBeforeCleanKnown);
        Assert.True(vm.WashMopBeforeClean);
    }

    [Fact]
    public void AnAbsentSettingLeavesTheLastKnownValueInPlace()
    {
        // Messages are partial: a CURRENT-STATE without "volume" says nothing about the volume.
        var vm = New(out _);
        vm.Apply(State("""{"msg":"CURRENT-STATE","volume":30,"alarm":true}"""));

        vm.Apply(State("""{"msg":"CURRENT-STATE","state":"FULL_CLEAN_RUNNING"}"""));

        Assert.Equal(30, vm.Volume);
        Assert.True(vm.Alarm);
    }
}
