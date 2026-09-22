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

    private static CleaningViewModel New(out RobotHub hub, params string[] maps)
    {
        var list = maps.Length > 0 ? maps : [TestHub.Map("1000000002", "Étage", isCurrent: true, Rooms)];
        hub = TestHub.Create(
            ("persistent-map-metadata", "[" + string.Join(",", list) + "]"),
            ("persistent-maps", TestHub.EmptyPersistentMap),
            ("live-maps/mapping", """{"dimensions":{"width":2,"height":2,"resolution":0.5,"offsetX":0,"offsetY":0},"mapData":[10,10,12,12]}"""),
            ("live-maps/cleaning", """{"cleanPath":[],"obstacles":[],"dirt":[]}"""));
        return new CleaningViewModel(hub, new MapCatalog(hub));
    }

    private static ZoneItem Room(CleaningViewModel vm, string id) => vm.Zones.First(z => z.Id == id);

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
    public async Task StartingWithNothingTickedSaysSoInsteadOfSendingACleanRequest()
    {
        var vm = New(out var hub);
        await vm.LoadMapsAsync();

        await vm.StartCleanCommand.ExecuteAsync(null);

        Assert.Equal("Sélectionnez au moins une pièce.", hub.Message);
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
}
