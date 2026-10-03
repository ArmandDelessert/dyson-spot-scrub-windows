using Dyss.Presentation.Services;
using Dyss.Presentation.ViewModels;

namespace Dyss.Presentation.Tests;

/// <summary>
/// The map manager, without a robot: the commands need an MQTT session, so what is checked here is
/// everything up to the wire — which map and rooms are in hand, what each click does in each mode,
/// which actions are on offer, and that nothing is sent when the answer is a cancel. "Robot non
/// connecté" in the status is the witness that a command did get as far as the robot.
/// </summary>
public class MapManagerViewModelTests
{
    private const string NotConnected = "Robot non connecté.";

    private const string Rooms = """
        {"id":"1000000002","zones":[
            {"id":"10","name":"Cuisine","type":"kitchen","area":29.7,"visited":[{"x":0,"y":0}]},
            {"id":"11","name":"Salle de bain","type":"toilet","area":4.7,"visited":[{"x":1,"y":0}]},
            {"id":"15","name":"Pièce1","type":"custom","area":5.0,"visited":[{"x":2,"y":0}]}],
         "dockLocation":{"x":0,"y":0,"angle":0}}
        """;

    /// <summary>What the prompts answer; every test gets its own.</summary>
    private readonly FakeDialogs _dialogs = new();

    private MapManagerViewModel New(bool firstIsActive = true)
    {
        var hub = TestHub.Create(_dialogs,
            ("persistent-map-metadata", "["
                + TestHub.Map("1000000002", "Étage", isCurrent: firstIsActive, ("10", "Cuisine", "kitchen")) + ","
                + TestHub.Map("1000000003", "Rez", isCurrent: false, ("20", "Salon", "livingRoom")) + "]"),
            ("persistent-maps", Rooms),
            ("live-maps/mapping", """{"dimensions":{"width":2,"height":2,"resolution":0.5,"offsetX":0,"offsetY":0},"mapData":[10,10,11,11]}"""));
        return new MapManagerViewModel(hub, new DisplaySettings());
    }

    private async Task<MapManagerViewModel> LoadedAsync(bool firstIsActive = true)
    {
        var vm = New(firstIsActive);
        await vm.LoadAsync("1000000002");
        await WaitForAsync(() => vm.Rooms.Count > 0);
        return vm;
    }

    private static ManagedRoom Room(MapManagerViewModel vm, string id) => vm.Rooms.Single(r => r.Id == id);

    // ---- Listing -------------------------------------------------------------------

    [Fact]
    public async Task RoomsShowTheirNameAndTheTypeOnlyWhenItAddsSomething()
    {
        var vm = await LoadedAsync();

        // The stored name, always: the W.-C.-typed room is called "Salle de bain" and shows as such.
        Assert.Equal(["Cuisine", "Pièce1", "Salle de bain"], vm.Rooms.Select(r => r.DisplayName));
        Assert.Equal("", Room(vm, "10").TypeText);              // name and type agree
        Assert.Equal("W.-C.", Room(vm, "11").TypeText);         // they differ
        Assert.Equal("Personnalisée", Room(vm, "15").TypeText); // custom, always said
        Assert.Equal("29.7 m²", Room(vm, "10").AreaText);
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
    public async Task AnInactiveMapIsDrawnFromItsVisitedPointsWithNoGrid()
    {
        // Only the active map has an occupancy grid; asking for one here would fetch another map's.
        var vm = await LoadedAsync(firstIsActive: false);

        Assert.Null(vm.Scene.Grid);
        Assert.Equal(3, vm.Scene.Map!.Zones!.Count);
    }

    // ---- Choosing a room -------------------------------------------------------------

    [Fact]
    public async Task AClickChoosesARoomAndASecondClickLetsGo()
    {
        var vm = await LoadedAsync();
        Assert.Null(vm.Scene.SelectedZoneIds);

        vm.RoomClickedById("11");
        Assert.Same(Room(vm, "11"), vm.SelectedRoom);
        Assert.True(Room(vm, "11").IsChosen);
        Assert.Equal(["11"], vm.Scene.SelectedZoneIds!);

        // Choosing another moves the highlight rather than adding to it.
        vm.RoomClicked(Room(vm, "10"));
        Assert.False(Room(vm, "11").IsChosen);
        Assert.Equal(["10"], vm.Scene.SelectedZoneIds!);

        vm.RoomClicked(Room(vm, "10"));
        Assert.Null(vm.SelectedRoom);
        Assert.Null(vm.Scene.SelectedZoneIds);
    }

    [Fact]
    public async Task ClickingEmptyMapSpaceLetsGoOfTheRoom()
    {
        var vm = await LoadedAsync();
        vm.RoomClickedById("10");

        vm.ClearRoomSelection();

        Assert.Null(vm.SelectedRoom);
        Assert.False(Room(vm, "10").IsChosen);
    }

    [Fact]
    public async Task ChangingMapForgetsTheRoomAndLeavesAnyMode()
    {
        var vm = await LoadedAsync();
        vm.RoomClickedById("10");
        vm.StartSplitCommand.Execute(null);
        Assert.True(vm.Splitting);

        vm.SelectedMap = vm.Maps.First(m => m.Id != vm.SelectedMap!.Id);

        // The room and the cut belonged to the other map; keeping them would aim at nothing.
        Assert.Null(vm.SelectedRoom);
        Assert.False(vm.Splitting);
        Assert.False(vm.InMode);
    }

    // ---- What is on offer -----------------------------------------------------------

    [Fact]
    public async Task NoActionIsOfferedUntilItHasWhatItNeeds()
    {
        var vm = await LoadedAsync();

        Assert.True(vm.RenameMapCommand.CanExecute(null));
        Assert.True(vm.DeleteMapCommand.CanExecute(null));
        Assert.False(vm.SetActiveCommand.CanExecute(null)); // already active
        // A merge can always be started on a map with two rooms; it is confirming it that waits.
        Assert.True(vm.MergeCommand.CanExecute(null));
        // Nothing is chosen among the rooms yet.
        Assert.False(vm.RenameRoomCommand.CanExecute(null));
        Assert.False(vm.StartSplitCommand.CanExecute(null));

        vm.RoomClickedById("10");
        Assert.True(vm.RenameRoomCommand.CanExecute(null));
        Assert.True(vm.StartSplitCommand.CanExecute(null));
    }

    [Fact]
    public async Task TheActiveMapIsNotOfferedAsSomethingToActivate()
    {
        var vm = await LoadedAsync(firstIsActive: true);

        Assert.True(vm.SelectedMap!.Metadata.IsCurrentMap);
        Assert.False(vm.SetActiveCommand.CanExecute(null));
        Assert.True(vm.RenameMapCommand.CanExecute(null));
    }

    [Fact]
    public async Task AnInactiveMapIsReadOnlyUntilMadeActive()
    {
        // Every edit ever captured targeted the active map, and editing an inactive one is the
        // likeliest cause of a map found renamed and emptied of rooms. Only activating is offered.
        var vm = await LoadedAsync(firstIsActive: false);
        vm.RoomClickedById("10");

        Assert.False(vm.IsActiveMap);
        Assert.True(vm.SetActiveCommand.CanExecute(null));
        Assert.False(vm.RenameMapCommand.CanExecute(null));
        Assert.False(vm.DeleteMapCommand.CanExecute(null));
        Assert.False(vm.RenameRoomCommand.CanExecute(null));
        Assert.False(vm.StartSplitCommand.CanExecute(null));
        Assert.False(vm.MergeCommand.CanExecute(null));
        Assert.StartsWith("Seule la carte active", vm.EditBlockedReason, StringComparison.Ordinal);
        // Rooms can still be looked at, only not changed.
        Assert.True(Room(vm, "10").IsChosen);
    }

    [Fact]
    public async Task TheActiveMapExplainsNothingBecauseNothingIsBlocked()
    {
        var vm = await LoadedAsync();

        Assert.True(vm.IsActiveMap);
        Assert.Equal("", vm.EditBlockedReason);
    }

    // ---- Merging -----------------------------------------------------------------------

    [Fact]
    public async Task TheFirstPressGathersRoomsAndTheSecondMerges()
    {
        var vm = await LoadedAsync();
        _dialogs.Confirm = (_, _) => true;

        await vm.MergeCommand.ExecuteAsync(null);
        Assert.True(vm.Merging);
        Assert.True(vm.InMode);
        // Nothing gathered yet: the button stays but cannot confirm.
        Assert.False(vm.MergeCommand.CanExecute(null));

        vm.RoomClickedById("10");
        Assert.False(vm.MergeCommand.CanExecute(null));
        vm.RoomClicked(Room(vm, "11"));
        Assert.True(vm.MergeCommand.CanExecute(null));
        Assert.Equal("Fusionner les 2 pièces", vm.MergeButtonLabel);
        // Both are highlighted at once, on the map as in the list.
        Assert.Equal(["10", "11"], vm.Scene.SelectedZoneIds!.Order());
        Assert.Null(vm.SelectedRoom);

        await vm.MergeCommand.ExecuteAsync(null);

        Assert.Equal(NotConnected, vm.Status);
        Assert.False(vm.Merging);
    }

    [Fact]
    public async Task WhileGatheringAClickTogglesARoomAndEmptySpaceLetsGoOfAll()
    {
        var vm = await LoadedAsync();
        await vm.MergeCommand.ExecuteAsync(null);
        vm.RoomClickedById("10");
        vm.RoomClickedById("11");

        vm.RoomClickedById("10");
        Assert.False(Room(vm, "10").IsChosen);
        Assert.True(Room(vm, "11").IsChosen);

        vm.ClearRoomSelection();
        Assert.DoesNotContain(vm.Rooms, r => r.IsChosen);
        Assert.True(vm.Merging); // still gathering, just with nothing gathered
    }

    [Fact]
    public async Task TheChosenRoomIsTheFirstOneGathered()
    {
        var vm = await LoadedAsync();
        vm.RoomClickedById("15");

        await vm.MergeCommand.ExecuteAsync(null);

        Assert.True(Room(vm, "15").IsChosen);
        Assert.Equal("Fusionner (choisissez 2 pièces ou plus)", vm.MergeButtonLabel);
    }

    [Fact]
    public async Task NothingElseIsOfferedWhileGatheringAndCancelPutsItAllBack()
    {
        var vm = await LoadedAsync();
        vm.RoomClickedById("10");
        await vm.MergeCommand.ExecuteAsync(null);

        Assert.False(vm.RenameRoomCommand.CanExecute(null));
        Assert.False(vm.StartSplitCommand.CanExecute(null));
        Assert.False(vm.RenameMapCommand.CanExecute(null));
        Assert.False(vm.DeleteMapCommand.CanExecute(null));

        vm.CancelCommand.Execute(null);

        Assert.False(vm.Merging);
        Assert.DoesNotContain(vm.Rooms, r => r.IsChosen);
        Assert.Equal("Fusionner des pièces…", vm.MergeButtonLabel);
        Assert.True(vm.RenameMapCommand.CanExecute(null));
    }

    [Fact]
    public async Task DecliningTheMergeConfirmationSendsNothing()
    {
        var vm = await LoadedAsync();
        _dialogs.Confirm = (_, _) => false;
        await vm.MergeCommand.ExecuteAsync(null);
        vm.RoomClickedById("10");
        vm.RoomClickedById("11");

        await vm.MergeCommand.ExecuteAsync(null);

        Assert.Equal("", vm.Status);
        Assert.True(vm.Merging); // still gathering, so the user can adjust and try again
    }

    // ---- Splitting ---------------------------------------------------------------------

    [Fact]
    public async Task SplittingNeedsARoomAndCancelLeavesIt()
    {
        var vm = await LoadedAsync();

        vm.StartSplitCommand.Execute(null);
        Assert.False(vm.Splitting);
        Assert.Equal("Choisissez d'abord la pièce à diviser.", vm.Status);

        vm.RoomClickedById("10");
        vm.StartSplitCommand.Execute(null);
        Assert.True(vm.Splitting);
        Assert.StartsWith("Cliquez les deux extrémités", vm.Hint, StringComparison.Ordinal);
        // While aiming the cut, clicks on rooms do not change which room is being cut.
        vm.RoomClickedById("11");
        Assert.Same(Room(vm, "10"), vm.SelectedRoom);

        vm.CancelCommand.Execute(null);
        Assert.False(vm.Splitting);
        Assert.StartsWith("Cliquez une pièce", vm.Hint, StringComparison.Ordinal);
    }

    // ---- Maps --------------------------------------------------------------------------

    [Fact]
    public async Task CancellingARenamePromptSendsNothing()
    {
        var vm = await LoadedAsync();
        _dialogs.AskText = (_, _, _) => null;

        await vm.RenameMapCommand.ExecuteAsync(null);

        Assert.Equal("", vm.Status);
    }

    [Fact]
    public async Task ARenameThatChangesNothingIsNotSentEither()
    {
        var vm = await LoadedAsync();
        _dialogs.AskText = (_, _, initial) => initial;

        await vm.RenameMapCommand.ExecuteAsync(null);

        Assert.Equal("", vm.Status);
    }

    [Fact]
    public async Task DeletingAMapAsksFirstAndSaysWhenItIsTheActiveOne()
    {
        var vm = await LoadedAsync(firstIsActive: true);
        string? asked = null;
        _dialogs.Confirm = (_, text) => { asked = text; return false; };

        await vm.DeleteMapCommand.ExecuteAsync(null);

        Assert.Contains("« Étage »", asked!, StringComparison.Ordinal);
        Assert.Contains("carte active", asked!, StringComparison.Ordinal);
        Assert.Equal("", vm.Status); // declined: nothing sent

        _dialogs.Confirm = (_, _) => true;
        await vm.DeleteMapCommand.ExecuteAsync(null);
        Assert.Equal(NotConnected, vm.Status);
    }

    [Fact]
    public async Task TheTypeListOffersTheThirtyTypesPlusAFreeName()
    {
        var vm = await LoadedAsync();

        Assert.Equal(31, vm.RoomTypes.Count);
        Assert.Null(vm.RoomTypes[0].Type); // the free-name option comes first
        Assert.Contains(vm.RoomTypes, t => t.Type == "bedroom" && t.Label == "Chambre");
    }

    private static async Task WaitForAsync(Func<bool> done)
    {
        for (var i = 0; i < 100 && !done(); i++) await Task.Delay(10);
        Assert.True(done(), "the map never finished loading");
    }
}
