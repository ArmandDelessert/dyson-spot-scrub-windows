using MyDyson.App.Services;
using MyDyson.App.ViewModels;
using MyDyson.Core;

namespace MyDyson.App.Tests;

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

        Assert.Equal(["Éviter la zone", "Aspirateur uniquement"], vm.RestrictionZones.Select(z => z.Label));
        Assert.Equal("4,0 × 4,0 m", vm.RestrictionZones[0].SizeText.Replace('.', ','));
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
        vm.ClickZoneCommand.Execute(vm.RestrictionZones[0]);

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
        vm.ClickZoneCommand.Execute(vm.RestrictionZones[1]);
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
        vm.ClickZoneCommand.Execute(vm.RestrictionZones[0]);

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
        vm.ClickZoneCommand.Execute(vm.RestrictionZones[0]);

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

    // ---- Furniture ------------------------------------------------------------------------

    [Fact]
    public async Task FurnitureIsListedAndChosenByClickingIt()
    {
        var vm = await LoadedAsync();
        vm.Layer = MapLayer.Furniture;

        Assert.Equal(["Réfrigérateur", "Lit double"], vm.FurnitureItems.Select(f => f.Label));
        Assert.False(vm.RotateFurnitureCommand.CanExecute(null));

        vm.MapClickedAt(new(21, 1));

        Assert.Equal("Lit double", vm.SelectedFurniture!.Label);
        Assert.Equal("4", vm.Scene.SelectedFurnitureId);
        Assert.True(vm.RotateFurnitureCommand.CanExecute(null));
        Assert.True(vm.MoveFurnitureCommand.CanExecute(null));
        Assert.True(vm.DeleteFurnitureCommand.CanExecute(null));
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
    public async Task PlacingShowsTheNewPieceAtItsSizeAndMovingShowsTheChosenOne()
    {
        var vm = await LoadedAsync();
        vm.Layer = MapLayer.Furniture;
        vm.FurnitureKind = FurnitureKind.FromRestType("wardrobe")!;

        vm.AddFurnitureCommand.Execute(null);
        Assert.Equal((1.0, 1.8), MapShapes.Sides(vm.PlacementShape!));
        Assert.Equal(new MyDyson.Core.Point(0, 0), MapShapes.Centre(vm.PlacementShape!));
        vm.CancelCommand.Execute(null);
        Assert.Null(vm.PlacementShape);

        vm.ClickFurnitureCommand.Execute(vm.FurnitureItems[1]);
        vm.MoveFurnitureCommand.Execute(null);
        Assert.Equal((2.1, 1.8), MapShapes.Sides(vm.PlacementShape!), new SidesComparer());
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

        vm.ClickFurnitureCommand.Execute(vm.FurnitureItems[0]);
        vm.Status = "";
        await vm.RotateFurnitureCommand.ExecuteAsync(null);
        Assert.Equal(NotConnected, vm.Status);

        vm.Status = "";
        await vm.DeleteFurnitureCommand.ExecuteAsync(null);
        Assert.Equal(NotConnected, vm.Status);
    }

    [Fact]
    public async Task APieceOfAnUnknownTypeLeavesAllFurnitureReadOnly()
    {
        var vm = await LoadedAsync(Objects.Replace("\"doubleBed\"", "\"uShapeCabinet\"", StringComparison.Ordinal));
        vm.Layer = MapLayer.Furniture;
        vm.ClickFurnitureCommand.Execute(vm.FurnitureItems[0]);

        Assert.Contains("Meuble inconnu (uShapeCabinet)", vm.FurnitureBlockedReason, StringComparison.Ordinal);
        Assert.False(vm.AddFurnitureCommand.CanExecute(null));
        Assert.False(vm.RotateFurnitureCommand.CanExecute(null));
    }

    /// <summary>Sides compared to the centimetre: they come out of square roots.</summary>
    private sealed class SidesComparer : IEqualityComparer<(double, double)>
    {
        public bool Equals((double, double) a, (double, double) b) => Math.Abs(a.Item1 - b.Item1) < 0.01 && Math.Abs(a.Item2 - b.Item2) < 0.01;
        public int GetHashCode((double, double) obj) => 0;
    }
}
