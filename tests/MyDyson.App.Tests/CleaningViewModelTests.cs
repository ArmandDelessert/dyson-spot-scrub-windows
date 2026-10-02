using MyDyson.App.Services;
using MyDyson.App.ViewModels;
using MyDyson.Core;

namespace MyDyson.App.Tests;

/// <summary>
/// Picking rooms and keeping that choice: the ordering badges, and what survives a reload — which
/// now happens on its own after a reconnection, so a wifi blip must not quietly undo what the user
/// was about to start a clean with.
/// </summary>
public class CleaningViewModelTests
{
    private static readonly (string Id, string Name, string? Type)[] Rooms =
        [("10", "Cuisine", "kitchen"), ("12", "Chambre", "bedroom"), ("13", "Couloir", "hallway")];

    private static CleaningViewModel New(out RobotHub hub, params string[] maps) => New(out hub, new DisplaySettings(), maps);

    private static CleaningViewModel New(out RobotHub hub, DisplaySettings display, params string[] maps)
    {
        var list = maps.Length > 0 ? maps : [TestHub.Map("1000000002", "Étage", isCurrent: true, Rooms)];
        hub = TestHub.Create(
            ("persistent-map-metadata", "[" + string.Join(",", list) + "]"),
            ("persistent-maps", TestHub.EmptyPersistentMap),
            ("live-maps/mapping", """{"dimensions":{"width":2,"height":2,"resolution":0.5,"offsetX":0,"offsetY":0},"mapData":[10,10,12,12]}"""),
            ("live-maps/cleaning", """{"cleanPath":[],"obstacles":[],"dirt":[]}"""));
        return new CleaningViewModel(hub, new MapCatalog(hub), display);
    }

    private static ZoneItem Room(CleaningViewModel vm, string id) => vm.Zones.First(z => z.Id == id);

    // ---- Cleaning a zone drawn on the map ------------------------------------------------------

    private static readonly RobotState Docked = RobotState.Parse("""{"msg":"CURRENT-STATE","state":"INACTIVE_CHARGING","dockState":"IDLE"}""")!;

    [Fact]
    public async Task AZoneIsDrawnWithTwoCornersAndShownOnTheMap()
    {
        var vm = New(out _);
        await vm.LoadMapsAsync();
        Assert.False(vm.HasSpot);

        vm.DrawSpotCommand.Execute(null);
        Assert.True(vm.DrawingSpot);
        Assert.Equal("Annuler le tracé", vm.DrawSpotLabel);

        vm.SpotDrawn(new(2, 1), new(0, 0));

        Assert.False(vm.DrawingSpot);
        Assert.True(vm.HasSpot);
        // The phone's corner order: top-right, top-left, bottom-left, bottom-right.
        Assert.Equal([new(2, 1), new(0, 1), new(0, 0), new(2, 0)], vm.SpotCorners!);
        Assert.Same(vm.SpotCorners, vm.Scene.SpotZone);
        Assert.Equal("Tracer une autre zone…", vm.DrawSpotLabel);
    }

    [Fact]
    public async Task ATooNarrowZoneIsRefused()
    {
        var vm = New(out var hub);
        await vm.LoadMapsAsync();
        vm.DrawSpotCommand.Execute(null);

        vm.SpotDrawn(new(0, 0), new(0.1, 2));

        Assert.False(vm.HasSpot);
        Assert.StartsWith("Zone trop étroite", hub.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AZoneCleanNeedsTheZoneAndARobotThatCanTakeIt()
    {
        var vm = New(out var hub);
        await vm.LoadMapsAsync();
        vm.Apply(Docked);
        Assert.False(vm.CanStartSpot);

        vm.SpotDrawn(new(0, 0), new(1, 1));
        Assert.True(vm.CanStartSpot);

        vm.Apply(RobotState.Parse("""{"msg":"CURRENT-STATE","state":"FULL_CLEAN_RUNNING"}""")!);
        Assert.False(vm.CanStartSpot);

        vm.Apply(Docked);
        await vm.StartSpotCleanCommand.ExecuteAsync(null);
        Assert.Equal("Robot non connecté.", hub.Message);   // got as far as the robot
    }

    [Fact]
    public async Task TheZoneSettingsFollowTheCleanTypeAndClearingForgetsTheZone()
    {
        var vm = New(out _);
        await vm.LoadMapsAsync();
        vm.SpotDrawn(new(0, 0), new(1, 1));

        Assert.True(vm.SpotHasVacuum);
        Assert.False(vm.SpotHasMop);
        vm.SpotCleanType = vm.SpotCleanTypes.Single(o => o.Value == CleanType.Mop);
        Assert.False(vm.SpotHasVacuum);
        Assert.True(vm.SpotHasMop);

        vm.ClearSpotCommand.Execute(null);
        Assert.False(vm.HasSpot);
        Assert.Null(vm.Scene.SpotZone);
    }

    [Fact]
    public async Task ADrawnZoneUnticksTheRoomsAndKeepsThemOutOfReach()
    {
        var vm = New(out _);
        await vm.LoadMapsAsync();
        vm.Apply(Docked);
        vm.ToggleZone("10");
        Assert.True(vm.RoomsEnabled);
        Assert.True(vm.CanStart);

        vm.SpotDrawn(new(0, 0), new(1, 1));

        Assert.False(vm.RoomsEnabled);
        Assert.False(Room(vm, "10").Selected);
        Assert.False(vm.CanStart);
        vm.ToggleZone("12");   // a click on a room of the map
        Assert.False(Room(vm, "12").Selected);

        vm.ClearSpotCommand.Execute(null);
        Assert.True(vm.RoomsEnabled);
        vm.ToggleZone("12");
        Assert.True(Room(vm, "12").Selected);
    }


    [Fact]
    public async Task EscapeGivesUpTheDrawingThenErasesTheZone()
    {
        var vm = New(out _);
        await vm.LoadMapsAsync();
        Assert.False(vm.CancelSpot());   // nothing to cancel: the key is left alone

        vm.DrawSpotCommand.Execute(null);
        Assert.True(vm.CancelSpot());
        Assert.False(vm.DrawingSpot);

        vm.SpotDrawn(new(0, 0), new(1, 1));
        vm.DrawSpotCommand.Execute(null);   // drawing another one: Escape keeps the first
        Assert.True(vm.CancelSpot());
        Assert.False(vm.DrawingSpot);
        Assert.True(vm.HasSpot);

        Assert.True(vm.CancelSpot());
        Assert.False(vm.HasSpot);
    }
    [Fact]
    public async Task AClickInEmptySpaceErasesTheZoneUnlessItLandsOnTheZone()
    {
        var vm = New(out _);
        await vm.LoadMapsAsync();
        vm.SpotDrawn(new(0, 0), new(1, 1));

        // The zone sticks out past the walls: a click on that part keeps it.
        vm.MapClickedAt(new(0.5, 0.5));
        vm.ClearSelection();
        Assert.True(vm.HasSpot);

        vm.MapClickedAt(new(3, 3));
        vm.ClearSelection();
        Assert.False(vm.HasSpot);
    }

    [Fact]
    public async Task AMovedOrStretchedZoneIsPutBackInThePhonesOrder()
    {
        var vm = New(out var hub);
        await vm.LoadMapsAsync();
        vm.SpotDrawn(new(0, 0), new(1, 1));

        // Stretched from a corner, the map hands the corners back in its own order.
        vm.SpotEdited([new(0, 0), new(0, 2), new(3, 2), new(3, 0)]);
        Assert.Equal([new(3, 2), new(0, 2), new(0, 0), new(3, 0)], vm.SpotCorners!);
        Assert.Same(vm.SpotCorners, vm.Scene.SpotZone);

        var before = vm.SpotCorners;
        vm.SpotEdited([new(0, 0), new(0, 2), new(0.1, 2), new(0.1, 0)]);
        Assert.StartsWith("Zone trop étroite", hub.Message, StringComparison.Ordinal);
        Assert.Equal(before, vm.SpotCorners!);
        Assert.NotSame(before, vm.SpotCorners);   // a fresh list, so the map redraws the zone where it was
    }

    [Fact]
    public async Task RoomsAreListedByNameWithTheTypesOwnLabel()
    {
        var vm = New(out _);

        await vm.LoadMapsAsync();

        // Sorted by display name, not by id and not in the order the cloud returned them.
        Assert.Equal(["Chambre", "Couloir", "Cuisine"], vm.Zones.Select(z => z.DisplayName));
    }

    [Fact]
    public async Task TickingRoomsNumbersThemInOrderAndUntickingClosesTheGap()
    {
        var vm = New(out _);
        await vm.LoadMapsAsync();

        vm.ToggleZone("10");
        vm.ToggleZone("12");
        vm.ToggleZone("13");
        Assert.Equal([1, 2, 3], new[] { Room(vm, "10").Order, Room(vm, "12").Order, Room(vm, "13").Order });

        // Drop the middle one: the ones after it move up so the badges stay 1, 2, 3…
        vm.ToggleZone("12");
        Assert.Equal(1, Room(vm, "10").Order);
        Assert.Equal(0, Room(vm, "12").Order);
        Assert.Equal(2, Room(vm, "13").Order);

        // And the next room ticked takes the free number rather than repeating one.
        vm.ToggleZone("12");
        Assert.Equal(3, Room(vm, "12").Order);
    }

    [Fact]
    public async Task ClearingTheSelectionUnticksEverything()
    {
        var vm = New(out _);
        await vm.LoadMapsAsync();
        vm.ToggleZone("10");
        vm.ToggleZone("13");

        vm.ClearSelection();

        Assert.DoesNotContain(vm.Zones, z => z.Selected);
        Assert.Empty(vm.Scene.SelectedZoneIds!);
    }

    [Fact]
    public async Task TheSceneCarriesTheSelectionAndItsOrderForTheBadges()
    {
        var vm = New(out _);
        await vm.LoadMapsAsync();

        vm.ToggleZone("13");
        vm.ToggleZone("10");

        Assert.Equal(["13", "10"], vm.Scene.SelectedZoneIds!.OrderBy(id => vm.Scene.ZoneOrder![id]));
        Assert.Equal(1, vm.Scene.ZoneOrder!["13"]);
        Assert.Equal(2, vm.Scene.ZoneOrder["10"]);
        // The active map's grid is attached; the rooms come from the metadata.
        Assert.NotNull(vm.Scene.Grid);
        Assert.Equal(3, vm.Scene.ZoneMetadata!.Count);
    }

    [Fact]
    public async Task AReloadKeepsTheRoomsTickedAndInTheSameOrder()
    {
        // A reconnection reloads the maps on its own now. Rebuilding the rows from fresh metadata
        // must not throw away a selection the user made.
        var vm = New(out _);
        await vm.LoadMapsAsync();
        vm.ToggleZone("13");
        vm.ToggleZone("10");

        await vm.LoadMapsAsync();

        Assert.Equal(["10", "13"], vm.Zones.Where(z => z.Selected).Select(z => z.Id).Order());
        Assert.Equal(1, Room(vm, "13").Order);
        Assert.Equal(2, Room(vm, "10").Order);
        Assert.False(Room(vm, "12").Selected);
        // And numbering carries on from there rather than colliding with a restored number.
        vm.ToggleZone("12");
        Assert.Equal(3, Room(vm, "12").Order);
    }

    [Fact]
    public async Task AReloadKeepsTheMapTheUserWasLookingAtRatherThanJumpingToTheActiveOne()
    {
        var vm = New(out _,
            TestHub.Map("1000000002", "Étage", isCurrent: true, Rooms),
            TestHub.Map("1000000003", "Rez", isCurrent: false, ("20", "Salon", "livingRoom")));
        await vm.LoadMapsAsync();
        Assert.Equal("1000000002", vm.SelectedMap!.Id); // the active map is the default

        vm.SelectedMap = vm.Maps.First(m => m.Id == "1000000003");
        await vm.LoadMapsAsync();

        Assert.Equal("1000000003", vm.SelectedMap!.Id);
        Assert.Equal("Salon", Assert.Single(vm.Zones).DisplayName);
    }

    [Fact]
    public async Task AMapThatDisappearedFallsBackToTheActiveOne()
    {
        var vm = New(out _,
            TestHub.Map("1000000002", "Étage", isCurrent: true, Rooms),
            TestHub.Map("1000000003", "Rez", isCurrent: false, ("20", "Salon", "livingRoom")));
        await vm.LoadMapsAsync();
        vm.SelectedMap = vm.Maps.First(m => m.Id == "1000000003");

        // The user deleted that map from the phone app in the meantime.
        var vm2 = New(out _, TestHub.Map("1000000002", "Étage", isCurrent: true, Rooms));
        vm2.SelectedMap = null;
        await vm2.LoadMapsAsync();

        Assert.Equal("1000000002", vm2.SelectedMap!.Id);
    }

    [Fact]
    public async Task AnInactiveMapGetsNeitherTheGridNorTheLivePosition()
    {
        // The occupancy grid and the robot's position only ever describe the active map; drawing
        // them over another one would overlay an unrelated task's stray path.
        var vm = New(out _,
            TestHub.Map("1000000002", "Étage", isCurrent: true, Rooms),
            TestHub.Map("1000000003", "Rez", isCurrent: false, ("20", "Salon", "livingRoom")));
        await vm.LoadMapsAsync();
        vm.Apply(RobotState.Parse("""{"msg":"CURRENT-STATE","globalPosition":[{"id":1,"x":1,"y":1,"angle":0,"update":1}]}""")!);
        Assert.NotNull(vm.Scene.Grid);
        Assert.NotNull(vm.Scene.Robot);

        vm.SelectedMap = vm.Maps.First(m => m.Id == "1000000003");

        Assert.Null(vm.Scene.Grid);
        Assert.Null(vm.Scene.Robot);
        Assert.Null(vm.Scene.Path);
    }

    [Fact]
    public async Task TheStartButtonNeedsBothAReadyRobotAndATickedRoom()
    {
        var vm = New(out _);
        await vm.LoadMapsAsync();
        Assert.False(vm.CanStart); // no state yet, nothing ticked

        vm.Apply(RobotState.Parse("""{"msg":"CURRENT-STATE","state":"INACTIVE_CHARGING","dockState":"IDLE"}""")!);
        Assert.False(vm.CanStart); // robot ready, still nothing ticked

        vm.ToggleZone("10");
        Assert.True(vm.CanStart);

        vm.ClearSelection();
        Assert.False(vm.CanStart);

        // And a robot in the middle of a clean cannot take another one, ticked rooms or not.
        vm.ToggleZone("10");
        vm.Apply(RobotState.Parse("""{"msg":"CURRENT-STATE","state":"FULL_CLEAN_RUNNING"}""")!);
        Assert.False(vm.CanStart);
    }

    [Fact]
    public async Task ASelectionRestoredByAReloadReEnablesTheStartButton()
    {
        var vm = New(out _);
        await vm.LoadMapsAsync();
        vm.Apply(RobotState.Parse("""{"msg":"CURRENT-STATE","state":"INACTIVE_CHARGING","dockState":"IDLE"}""")!);
        vm.ToggleZone("10");

        await vm.LoadMapsAsync();

        Assert.True(vm.CanStart);
    }

    [Fact]
    public async Task StartingWithNothingTickedSaysSoInsteadOfSendingACleanRequest()
    {
        // The button is disabled in that state, so this is the belt to its braces.
        var vm = New(out var hub);
        await vm.LoadMapsAsync();

        await vm.StartCleanCommand.ExecuteAsync(null);

        Assert.Equal("Sélectionnez au moins une pièce.", hub.Message);
    }

    [Fact]
    public async Task TheDisplayPreferencesReachTheScene()
    {
        var display = new DisplaySettings();
        var vm = New(out _, display);
        await vm.LoadMapsAsync();
        Assert.True(vm.Scene.ShowFurniture);
        Assert.True(vm.Scene.ShowTravelPath);

        display.ShowFurniture = false;
        display.ShowTravelPath = false;
        vm.RebuildScene();

        Assert.False(vm.Scene.ShowFurniture);
        Assert.False(vm.Scene.ShowTravelPath);
    }

    [Fact]
    public async Task TheLiveTrailReplacesTheSnapshotFetchedAtStartup()
    {
        var vm = New(out _);
        await vm.LoadMapsAsync();

        vm.SetLiveTrail([new RobotPosition(1, 0.5, 0.5, 0, 1), new RobotPosition(2, 0.6, 0.5, 0, 0)]);

        Assert.Equal(2, vm.Scene.Path!.Count);
        Assert.Equal(1, vm.Scene.Path[0].Update);
        Assert.Equal(0, vm.Scene.Path[1].Update);
    }

    [Fact]
    public async Task OnceTheRobotIsDoneTheTrailLeavesTheMapAndTheHistoryIsTold()
    {
        var vm = New(out _);
        await vm.LoadMapsAsync();
        var finished = 0;
        vm.TaskFinished += () => finished++;

        // On launch with the robot already back: no trail, and nothing new for the history.
        vm.Apply(Docked);
        vm.SetLiveTrail([new RobotPosition(1, 0.5, 0.5, 0, 1)]);
        Assert.Null(vm.Scene.Path);
        Assert.Equal(0, finished);

        vm.Apply(RobotState.Parse("""{"msg":"CURRENT-STATE","state":"FULL_CLEAN_RUNNING","dockState":"IDLE","fullCleanAction":"VACUUMING"}""")!);
        Assert.NotNull(vm.Scene.Path);

        // Back on the dock, emptying the bin: still part of the task.
        vm.Apply(RobotState.Parse("""{"msg":"CURRENT-STATE","state":"FULL_CLEAN_FINISHED","dockState":"COLLECTING_DUST"}""")!);
        Assert.NotNull(vm.Scene.Path);
        Assert.Equal(0, finished);

        vm.Apply(Docked);
        Assert.Null(vm.Scene.Path);
        Assert.Equal(1, finished);
        vm.Apply(Docked);
        Assert.Equal(1, finished);
    }
}
