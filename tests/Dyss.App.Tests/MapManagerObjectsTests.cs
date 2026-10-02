using Dyss.App.Services;
using Dyss.App.ViewModels;
using Dyss.Core;

namespace Dyss.App.Tests;

/// <summary>
/// The Zones and Meubles tabs of the map manager, without a robot: what is listed, what a click on
/// the map chooses on each tab, which actions are on offer, and when the whole layer has to stay
/// read-only. "Robot non connecté" in the status is the witness that a change got as far as the wire.
/// </summary>
public class MapManagerObjectsTests
{
    private const string NotConnected = "Robot non connecté.";

    // One zone of each of two kinds, one inside the other; a fridge and a bed.
    private const string Objects = """
        {"id":"1000000002","zones":[{"id":"10","name":"Cuisine","type":"kitchen","area":29.7,"visited":[{"x":0,"y":0}]}],
         "restrictions":[
            {"id":"1","behavior":"keepOut","points":[{"x":0,"y":4},{"x":0,"y":0},{"x":4,"y":0},{"x":4,"y":4}]},
            {"id":"2","behavior":"noMop","points":[{"x":1,"y":2},{"x":1,"y":1},{"x":2,"y":1},{"x":2,"y":2}]}],
         "furniture":[
            {"id":"1","type":"refrigerator","userDefined":true,"points":[{"x":10,"y":1},{"x":10,"y":0},{"x":11,"y":0},{"x":11,"y":1}]},
            {"id":"4","type":"doubleBed","userDefined":true,"points":[{"x":20,"y":2.1},{"x":20,"y":0},{"x":21.8,"y":0},{"x":21.8,"y":2.1}]}],
         "dockLocation":{"x":0,"y":0,"angle":0}}
        """;

    private static async Task<MapManagerViewModel> LoadedAsync(string map = Objects, bool active = true, DisplaySettings? display = null)
    {
        var hub = TestHub.Create(
            ("persistent-map-metadata", "[" + TestHub.Map("1000000002", "Étage", isCurrent: active, ("10", "Cuisine", "kitchen")) + "]"),
            ("persistent-maps", map),
            ("live-maps/mapping", """{"dimensions":{"width":2,"height":2,"resolution":0.5,"offsetX":0,"offsetY":0},"mapData":[10,10,10,10]}"""));
        var vm = new MapManagerViewModel(hub, display ?? new DisplaySettings());
        await vm.LoadAsync("1000000002");
        for (var i = 0; i < 100 && vm.Rooms.Count == 0; i++) await Task.Delay(10);
        return vm;
    }

    // ---- Zones ----------------------------------------------------------------------------

    [Fact]
    public async Task ZonesAreListedWithTheirKindAndSize()
    {
        var vm = await LoadedAsync();

        // By name, whatever order the cloud lists them in.
        Assert.Equal(["Aspirateur uniquement", "Zone à éviter"], vm.RestrictionZones.Select(z => z.Label));
        Assert.Equal("4,0 × 4,0 m", Zone(vm, "1").SizeText.Replace('.', ','));
        Assert.Equal("", vm.ZonesEmptyText);
        Assert.Equal("", vm.ZonesBlockedReason);
    }

    [Fact]
    public async Task OnTheZonesTabAClickChoosesTheSmallestZoneUnderItAndNotARoom()
    {
        var vm = await LoadedAsync();
        vm.Layer = MapLayer.Zones;

        vm.RoomClickedById("10");
        Assert.Null(vm.SelectedRoom);

        vm.MapClickedAt(new(1.5, 1.5));   // inside both: the small one wins
        Assert.Equal("2", vm.SelectedZone!.Id);
        Assert.Equal("2", vm.Scene.SelectedRestrictionId);
        // The kind picker follows the chosen zone, so there is nothing to change yet.
        Assert.Equal("noMop", vm.ZoneKind.Behavior);
        Assert.False(vm.ChangeZoneKindCommand.CanExecute(null));

        vm.MapClickedAt(new(3, 3));
        Assert.Equal("1", vm.SelectedZone!.Id);

        vm.MapClickedAt(new(9, 9));   // empty space lets go
        Assert.Null(vm.SelectedZone);
    }

    [Fact]
    public async Task OnTheRoomsTabAMapClickLeavesZonesAlone()
    {
        var vm = await LoadedAsync();

        vm.MapClickedAt(new(1.5, 1.5));

        Assert.Null(vm.SelectedZone);
    }

    [Fact]
    public async Task ChangingTabLetsGoOfWhatWasChosenAndOfAnyMode()
    {
        var vm = await LoadedAsync();
        vm.RoomClickedById("10");
        vm.Layer = MapLayer.Zones;
        Assert.Null(vm.SelectedRoom);
        vm.AddZoneCommand.Execute(null);
        Assert.True(vm.InMode);

        vm.Layer = MapLayer.Furniture;

        Assert.False(vm.AddingZone);
        Assert.False(vm.InMode);
    }

    [Fact]
    public async Task PickingAnotherKindOffersToChangeTheChosenZone()
    {
        var vm = await LoadedAsync();
        vm.Layer = MapLayer.Zones;
        vm.ClickZoneCommand.Execute(Zone(vm, "1"));

        vm.ZoneKind = RestrictionKind.FromBehavior("climbObstacle")!;

        Assert.True(vm.ChangeZoneKindCommand.CanExecute(null));
        await vm.ChangeZoneKindCommand.ExecuteAsync(null);
        Assert.Equal(NotConnected, vm.Status);
    }

    [Fact]
    public async Task DrawingAZoneNeedsTwoCornersFarEnoughApart()
    {
        var vm = await LoadedAsync();
        vm.Layer = MapLayer.Zones;
        vm.AddZoneCommand.Execute(null);
        Assert.True(vm.AddingZone);
        Assert.False(vm.DeleteZoneCommand.CanExecute(null));   // nothing else while drawing

        await vm.ZoneDrawnAsync(new(0, 0), new(0.1, 3));
        Assert.StartsWith("Zone trop étroite", vm.Status, StringComparison.Ordinal);
        Assert.False(vm.AddingZone);

        vm.AddZoneCommand.Execute(null);
        await vm.ZoneDrawnAsync(new(0, 0), new(1, 1));
        Assert.Equal(NotConnected, vm.Status);
    }

    [Fact]
    public async Task DecliningToDeleteAZoneSendsNothing()
    {
        var vm = await LoadedAsync();
        vm.Layer = MapLayer.Zones;
        vm.ClickZoneCommand.Execute(Zone(vm, "2"));
        string? asked = null;
        vm.Confirm = (_, text) => { asked = text; return false; };

        await vm.DeleteZoneCommand.ExecuteAsync(null);

        Assert.Contains("Aspirateur uniquement", asked, StringComparison.Ordinal);
        Assert.Equal("", vm.Status);
    }

    [Fact]
    public async Task AZoneOfAnUnknownKindLeavesAllZonesReadOnly()
    {
        // Resending the list without it would delete it; with it is impossible, its type unknown.
        var vm = await LoadedAsync(Objects.Replace("\"noMop\"", "\"brushBarAndTraction\"", StringComparison.Ordinal));
        vm.Layer = MapLayer.Zones;
        vm.ClickZoneCommand.Execute(Zone(vm, "1"));

        Assert.Contains("Type inconnu (brushBarAndTraction)", vm.ZonesBlockedReason, StringComparison.Ordinal);
        Assert.False(vm.AddZoneCommand.CanExecute(null));
        Assert.False(vm.DeleteZoneCommand.CanExecute(null));
        // Furniture is a separate list and stays editable.
        Assert.True(vm.AddFurnitureCommand.CanExecute(null));
    }

    [Fact]
    public async Task OnlyTheActiveMapsZonesAndFurnitureCanBeChanged()
    {
        var vm = await LoadedAsync(active: false);
        vm.Layer = MapLayer.Zones;
        vm.ClickZoneCommand.Execute(Zone(vm, "1"));

        Assert.False(vm.AddZoneCommand.CanExecute(null));
        Assert.False(vm.DeleteZoneCommand.CanExecute(null));
        Assert.False(vm.AddFurnitureCommand.CanExecute(null));
    }

    [Fact]
    public async Task AMapWithoutZonesSaysSo()
    {
        var vm = await LoadedAsync(TestHub.EmptyPersistentMap.Replace("\"zones\":[]", "\"zones\":[{\"id\":\"10\",\"name\":\"Cuisine\",\"visited\":[{\"x\":0,\"y\":0}]}]", StringComparison.Ordinal));

        Assert.Equal("Aucune zone sur cette carte.", vm.ZonesEmptyText);
        Assert.Equal("Aucun meuble sur cette carte.", vm.FurnitureEmptyText);
        Assert.True(vm.AddZoneCommand.CanExecute(null));
    }

    // ---- Dragging a zone -------------------------------------------------------------------

    [Fact]
    public async Task TheChosenZoneCanBeDraggedAndResizedButNotBelowTwentyCentimetres()
    {
        var vm = await LoadedAsync();
        vm.Layer = MapLayer.Zones;
        Assert.Null(vm.EditableShape);

        vm.ClickZoneCommand.Execute(Zone(vm, "2"));
        Assert.Same(Zone(vm, "2").Corners, vm.EditableShape);
        Assert.True(vm.EditableShapeResizable);

        await vm.ShapeDroppedAsync(MapShapes.Rectangle(new(1, 1), new(1.1, 3)));
        Assert.StartsWith("Zone trop étroite", vm.Status, StringComparison.Ordinal);

        await vm.ShapeDroppedAsync(MapShapes.Rectangle(new(5, 5), new(7, 6)));
        Assert.Equal(NotConnected, vm.Status);
    }

    [Fact]
    public async Task NothingCanBeDraggedOnAnotherMapOrWhileDrawing()
    {
        var inactive = await LoadedAsync(active: false);
        inactive.Layer = MapLayer.Zones;
        inactive.ClickZoneCommand.Execute(Zone(inactive, "1"));
        Assert.Null(inactive.EditableShape);

        var vm = await LoadedAsync();
        vm.Layer = MapLayer.Zones;
        vm.ClickZoneCommand.Execute(Zone(vm, "1"));
        vm.AddZoneCommand.Execute(null);
        Assert.Null(vm.EditableShape);
    }

    // ---- Furniture ------------------------------------------------------------------------

    [Fact]
    public async Task FurnitureIsListedByNameAndChosenByClickingIt()
    {
        var vm = await LoadedAsync();
        vm.Layer = MapLayer.Furniture;

        Assert.Equal(["Lit double", "Réfrigérateur"], vm.FurnitureItems.Select(f => f.Label));
        Assert.False(vm.CanOrientFurniture);
        Assert.Null(vm.FurnitureOrientation);

        vm.MapClickedAt(new(21, 1));

        Assert.Equal("Lit double", vm.SelectedFurniture!.Label);
        Assert.Equal("4", vm.Scene.SelectedFurnitureId);
        Assert.True(vm.CanOrientFurniture);
        Assert.True(vm.DeleteFurnitureCommand.CanExecute(null));
        // Dragged as it stands, never resized.
        Assert.Same(Piece(vm, "4").Corners, vm.EditableShape);
        Assert.False(vm.EditableShapeResizable);
    }

    [Fact]
    public async Task TheOrientationListShowsHowTheChosenPieceFaces()
    {
        // The bed stands as a new piece is laid out; the second one is turned a quarter clockwise.
        var turned = Objects.Replace(
            """{"x":20,"y":2.1},{"x":20,"y":0},{"x":21.8,"y":0},{"x":21.8,"y":2.1}""",
            """{"x":22,"y":1},{"x":20,"y":1},{"x":20,"y":0},{"x":22,"y":0}""", StringComparison.Ordinal);
        var vm = await LoadedAsync(turned);
        vm.Layer = MapLayer.Furniture;

        vm.ClickFurnitureCommand.Execute(Piece(vm, "1"));
        Assert.Equal(0, vm.FurnitureOrientation!.QuarterTurns);

        vm.ClickFurnitureCommand.Execute(Piece(vm, "4"));
        Assert.Equal(1, vm.FurnitureOrientation!.QuarterTurns);
        // Showing it sent nothing.
        Assert.Equal("", vm.Status);
    }

    [Fact]
    public async Task TheFurnitureTabShowsFurnitureEvenWhenTheDisplayHidesIt()
    {
        var vm = await LoadedAsync(display: new DisplaySettings { ShowFurniture = false });
        Assert.False(vm.Scene.ShowFurniture);

        vm.Layer = MapLayer.Furniture;

        Assert.True(vm.Scene.ShowFurniture);
    }

    [Fact]
    public async Task PlacingShowsTheNewPieceAtItsSize()
    {
        var vm = await LoadedAsync();
        vm.Layer = MapLayer.Furniture;
        vm.FurnitureKind = FurnitureKind.FromRestType("wardrobe")!;

        vm.AddFurnitureCommand.Execute(null);
        Assert.Equal((1.0, 1.8), MapShapes.Sides(vm.PlacementShape!));
        Assert.Equal(new Dyss.Core.Point(0, 0), MapShapes.Centre(vm.PlacementShape!));

        vm.CancelCommand.Execute(null);
        Assert.Null(vm.PlacementShape);
    }

    [Fact]
    public async Task EveryFurnitureChangeGetsAsFarAsTheRobot()
    {
        var vm = await LoadedAsync();
        vm.Layer = MapLayer.Furniture;
        vm.Confirm = (_, _) => true;

        vm.AddFurnitureCommand.Execute(null);
        await vm.FurniturePointPickedAsync(new(5, 5));
        Assert.Equal(NotConnected, vm.Status);

        vm.ClickFurnitureCommand.Execute(Piece(vm, "1"));
        vm.Status = "";
        await vm.ShapeDroppedAsync(MapShapes.Translate(Piece(vm, "1").Corners, 2, 0));
        Assert.Equal(NotConnected, vm.Status);

        vm.Status = "";
        vm.FurnitureOrientation = vm.Orientations[2];
        for (var i = 0; i < 50 && vm.Status == ""; i++) await Task.Delay(10);
        Assert.Equal(NotConnected, vm.Status);
        // Not sent, so the list shows the piece as it really stands again.
        Assert.Equal(0, vm.FurnitureOrientation!.QuarterTurns);

        vm.Status = "";
        await vm.DeleteFurnitureCommand.ExecuteAsync(null);
        Assert.Equal(NotConnected, vm.Status);
    }

    [Fact]
    public async Task APieceOfAnUnknownTypeLeavesAllFurnitureReadOnly()
    {
        var vm = await LoadedAsync(Objects.Replace("\"doubleBed\"", "\"uShapeCabinet\"", StringComparison.Ordinal));
        vm.Layer = MapLayer.Furniture;
        vm.ClickFurnitureCommand.Execute(Piece(vm, "1"));

        Assert.Contains("Meuble inconnu (uShapeCabinet)", vm.FurnitureBlockedReason, StringComparison.Ordinal);
        Assert.False(vm.AddFurnitureCommand.CanExecute(null));
        Assert.False(vm.CanOrientFurniture);
        Assert.Null(vm.EditableShape);
    }

    [Fact]
    public async Task TheGridBoxDecidesForZonesAndFurnitureButACutAlwaysSnaps()
    {
        var display = new DisplaySettings();
        var vm = await LoadedAsync(display: display);
        Assert.True(vm.SnapToGrid);   // on unless unticked
        Assert.True(vm.GridActive);

        vm.SnapToGrid = false;
        Assert.False(display.SnapToGrid);
        Assert.False(vm.GridActive);

        vm.ClickRoomCommand.Execute(vm.Rooms[0]);
        vm.StartSplitCommand.Execute(null);
        Assert.True(vm.GridActive);
        vm.CancelCommand.Execute(null);
        Assert.False(vm.GridActive);
    }

    // ---- Orientation -------------------------------------------------------------------------

    [Fact]
    public async Task PickingAnotherOrientationTurnsTheMapOnTheCloud()
    {
        var vm = await LoadedAsync(Objects.Replace("\"id\":\"1000000002\",", "\"id\":\"1000000002\",\"orientation\":90,", StringComparison.Ordinal));
        Assert.Equal(1, vm.MapOrientation!.QuarterTurns);   // shown as the map stands, nothing sent
        Assert.Equal("", vm.Status);
        Assert.True(vm.CanRotateMap);
        var changed = false;
        vm.Changed += () => changed = true;

        vm.MapOrientation = vm.MapOrientations[2];
        for (var i = 0; i < 100 && vm.Status == ""; i++) await Task.Delay(10);

        Assert.Equal("Carte tournée à 180°.", vm.Status);
        Assert.True(changed);
        // The canned map still says 90: the list shows what the cloud gives back, not the pick.
        Assert.Equal(1, vm.MapOrientation!.QuarterTurns);
    }

    [Fact]
    public async Task AnInactiveMapCanBeTurnedToo()
    {
        var vm = await LoadedAsync(active: false);

        Assert.True(vm.CanRotateMap);
        Assert.Equal(0, vm.MapOrientation!.QuarterTurns);
    }

    private static ManagedZone Zone(MapManagerViewModel vm, string id) => vm.RestrictionZones.Single(z => z.Id == id);
    private static ManagedFurniture Piece(MapManagerViewModel vm, string id) => vm.FurnitureItems.Single(f => f.Id == id);
}
