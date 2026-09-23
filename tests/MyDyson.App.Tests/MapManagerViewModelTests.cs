using MyDyson.App.Services;
using MyDyson.App.ViewModels;

namespace MyDyson.App.Tests;

/// <summary>
/// The map manager, without a robot: the commands need an MQTT session, so what is checked here is
/// everything up to the wire — which map and room are in hand, what the prompts are asked, and that
/// nothing is sent when the answer is a cancel.
/// </summary>
public class MapManagerViewModelTests
{
    private const string Rooms = """
        {"id":"1000000002","zones":[
            {"id":"10","name":"Cuisine","type":"kitchen","area":29.7,"visited":[{"x":0,"y":0}]},
            {"id":"11","name":"Salle de bain","type":"toilet","area":4.7,"visited":[{"x":1,"y":0}]},
            {"id":"15","name":"Pièce1","type":"custom","area":5.0,"visited":[{"x":2,"y":0}]}],
         "dockLocation":{"x":0,"y":0,"angle":0}}
        """;

    private static MapManagerViewModel New(out RobotHub hub)
    {
        hub = TestHub.Create(
            ("persistent-map-metadata", "[" + TestHub.Map("1000000002", "Étage", isCurrent: false, ("10", "Cuisine", "kitchen")) + "," + TestHub.Map("1000000003", "Rez", isCurrent: false, ("20", "Salon", "livingRoom")) + "]"),
            ("persistent-maps", Rooms));
        return new MapManagerViewModel(hub, new DisplaySettings());
    }

    [Fact]
    public async Task TheRoomsOfTheChosenMapAreListedWithTheirDisplayName()
    {
        var vm = New(out _);

        await vm.LoadAsync();

        Assert.Equal("1000000002", vm.SelectedMap!.Id);
        // Sorted by what is shown, and a room typed W.-C. reads as its type even though the name
        // Dyson stored on it is "Salle de bain".
        Assert.Equal(["Cuisine", "Pièce1", "W.-C."], vm.Rooms.Select(r => r.DisplayName));
        Assert.Equal("personnalisée", vm.Rooms.Single(r => r.Id == "15").TypeText);
        // The type is not repeated beside a room already named after it.
        Assert.Equal("", vm.Rooms.Single(r => r.Id == "10").TypeText);
        Assert.Equal("29.7 m²", vm.Rooms.Single(r => r.Id == "10").AreaText);
    }

    [Fact]
    public async Task OnlyTheChosenRoomIsHighlightedOnTheMap()
    {
        var vm = New(out _);
        await vm.LoadAsync();
        Assert.Null(vm.Scene.SelectedZoneIds);

        vm.SelectRoomById("11");

        Assert.Equal("W.-C.", vm.SelectedRoom!.DisplayName);
        Assert.Equal(["11"], vm.Scene.SelectedZoneIds!);
    }

    [Fact]
    public async Task AnInactiveMapIsDrawnFromItsVisitedPointsWithNoGrid()
    {
        // Only the active map has an occupancy grid; asking for one here would fetch another map's.
        var vm = New(out _);

        await vm.LoadAsync();

        Assert.Null(vm.Scene.Grid);
        Assert.Equal(3, vm.Scene.Map!.Zones!.Count);
    }

    [Fact]
    public async Task CancellingARenamePromptSendsNothing()
    {
        var vm = New(out var hub);
        await vm.LoadAsync();
        vm.AskForText = (_, _, _) => null;

        await vm.RenameMapCommand.ExecuteAsync(null);

        // Reaching the robot would have set this, there being no session behind the test hub.
        Assert.Equal("", vm.Status);
        Assert.Equal("", hub.Message);
    }

    [Fact]
    public async Task ARenameThatChangesNothingIsNotSentEither()
    {
        var vm = New(out _);
        await vm.LoadAsync();
        vm.AskForText = (_, _, initial) => initial;

        await vm.RenameMapCommand.ExecuteAsync(null);

        Assert.Equal("", vm.Status);
    }

    [Fact]
    public async Task MergingNeedsTwoRoomsTicked()
    {
        var vm = New(out _);
        await vm.LoadAsync();
        vm.Confirm = (_, _) => true;

        await vm.MergeRoomsCommand.ExecuteAsync(null);
        Assert.Equal("Cochez au moins deux pièces à fusionner.", vm.Status);

        vm.Rooms[0].Picked = true;
        await vm.MergeRoomsCommand.ExecuteAsync(null);
        Assert.Equal("Cochez au moins deux pièces à fusionner.", vm.Status);

        // With two ticked it gets as far as the robot, which is not there in a test.
        vm.Rooms[1].Picked = true;
        await vm.MergeRoomsCommand.ExecuteAsync(null);
        Assert.Equal("Robot non connecté.", vm.Status);
    }

    [Fact]
    public async Task SplittingNeedsARoomChosenFirst()
    {
        var vm = New(out _);
        await vm.LoadAsync();

        vm.StartSplitCommand.Execute(null);
        Assert.False(vm.Splitting);
        Assert.Equal("Choisissez d'abord la pièce à diviser.", vm.Status);

        vm.SelectRoomById("10");
        vm.StartSplitCommand.Execute(null);
        Assert.True(vm.Splitting);
        Assert.NotEqual("", vm.SplitHint);

        vm.CancelSplitCommand.Execute(null);
        Assert.False(vm.Splitting);
        Assert.Equal("", vm.SplitHint);
    }

    [Fact]
    public async Task ChangingMapLeavesTheSplitModeBehind()
    {
        // The pending cut belongs to a room of the map that was on screen; carrying it over to
        // another map would aim it at nothing.
        var vm = New(out _);
        await vm.LoadAsync();
        vm.SelectRoomById("10");
        vm.StartSplitCommand.Execute(null);

        vm.SelectedMap = vm.Maps.First(m => m.Id != vm.SelectedMap!.Id);

        Assert.False(vm.Splitting);
    }

    [Fact]
    public async Task NoActionIsOfferedUntilItHasWhatItNeeds()
    {
        var vm = New(out _);
        await vm.LoadAsync();

        // A map is always chosen once loaded, so its two actions are live — except making the
        // active map active again.
        Assert.True(vm.RenameMapCommand.CanExecute(null));
        Assert.True(vm.SetActiveCommand.CanExecute(null));
        // Nothing is chosen among the rooms yet.
        Assert.False(vm.RenameRoomCommand.CanExecute(null));
        Assert.False(vm.StartSplitCommand.CanExecute(null));
        Assert.False(vm.MergeRoomsCommand.CanExecute(null));

        vm.SelectRoomById("10");
        Assert.True(vm.RenameRoomCommand.CanExecute(null));
        Assert.True(vm.StartSplitCommand.CanExecute(null));

        // Merging needs two, and says so by staying unavailable with one.
        vm.Rooms[0].Picked = true;
        Assert.False(vm.MergeRoomsCommand.CanExecute(null));
        vm.Rooms[1].Picked = true;
        Assert.True(vm.MergeRoomsCommand.CanExecute(null));
    }

    [Fact]
    public async Task TheActiveMapIsNotOfferedAsSomethingToActivate()
    {
        var hub = TestHub.Create(
            ("persistent-map-metadata", "[" + TestHub.Map("1000000002", "Étage", isCurrent: true, ("10", "Cuisine", "kitchen")) + "]"),
            ("persistent-maps", Rooms),
            ("live-maps/mapping", """{"dimensions":{"width":2,"height":2,"resolution":0.5,"offsetX":0,"offsetY":0},"mapData":[10,10,11,11]}"""));
        var vm = new MapManagerViewModel(hub, new DisplaySettings());

        await vm.LoadAsync();

        Assert.True(vm.SelectedMap!.Metadata.IsCurrentMap);
        Assert.False(vm.SetActiveCommand.CanExecute(null));
        Assert.True(vm.RenameMapCommand.CanExecute(null));
    }

    [Fact]
    public async Task ChangingMapForgetsTheRoomThatWasChosen()
    {
        var vm = New(out _);
        await vm.LoadAsync();
        vm.SelectRoomById("10");

        vm.SelectedMap = vm.Maps.First(m => m.Id != vm.SelectedMap!.Id);

        // The room belonged to the other map; keeping it would aim the next action at nothing.
        Assert.Null(vm.SelectedRoom);
        Assert.False(vm.RenameRoomCommand.CanExecute(null));
    }

    [Fact]
    public async Task TheActiveMapComesFirstThenTheRestByName()
    {
        var hub = TestHub.Create(
            ("persistent-map-metadata", "[" 
                + TestHub.Map("3", "Zèbre", isCurrent: false, ("10", "Cuisine", "kitchen")) + ","
                + TestHub.Map("1", "Alpha", isCurrent: false, ("10", "Cuisine", "kitchen")) + ","
                + TestHub.Map("2", "Milieu", isCurrent: true, ("10", "Cuisine", "kitchen")) + "]"),
            ("persistent-maps", Rooms),
            ("live-maps/mapping", """{"dimensions":{"width":2,"height":2,"resolution":0.5,"offsetX":0,"offsetY":0},"mapData":[10,10,11,11]}"""));
        var vm = new MapManagerViewModel(hub, new DisplaySettings());

        await vm.LoadAsync();

        Assert.Equal(["2", "1", "3"], vm.Maps.Select(m => m.Id));
    }

    [Fact]
    public async Task TheTypeListOffersTheThirtyTypesPlusAFreeName()
    {
        var vm = New(out _);
        await vm.LoadAsync();

        Assert.Equal(31, vm.RoomTypes.Count);
        Assert.Null(vm.RoomTypes[0].Type); // the free-name option comes first
        Assert.Contains(vm.RoomTypes, t => t.Type == "bedroom" && t.Label == "Chambre");
    }
}
