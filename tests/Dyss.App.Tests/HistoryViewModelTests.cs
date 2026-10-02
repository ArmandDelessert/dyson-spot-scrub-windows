using Dyss.App.Services;
using Dyss.App.ViewModels;

namespace Dyss.App.Tests;

/// <summary>
/// The Historique tab. The interesting part is which rooms a past clean covered: the only honest
/// source is each zone's cleanStatus in that clean's own detail (see docs/protocole.md), and the
/// detail is several hundred KB, so it is fetched once and shared.
/// </summary>
public class HistoryViewModelTests
{
    private const string OneClean = """
        {"data":[{"cleanId":"clean-1","persistentMapId":"1000000002","startTime":1789916722,"endTime":1789924402,
                  "cleanDuration":128,"areaCleaned":87.1,"startBattery":91.0,"endBattery":26.0,"faults":[]}]}
        """;

    // Cuisine cleaned, Couloir unreachable, Chambre never got its turn, Pièce1 not part of it.
    private const string Detail = """
        {"cleanId":"clean-1","persistentMapId":"1000000002",
         "zones":[{"id":"10","name":"Cuisine","type":"kitchen","cleanStatus":"CLEAN_COMPLETE"},
                  {"id":"13","name":"Couloir","type":"hallway","cleanStatus":"CANT_CLEAN"},
                  {"id":"12","name":"Chambre","type":"bedroom","cleanStatus":"CLEAN_PENDING"},
                  {"id":"15","name":"Pièce1","type":null,"cleanStatus":"CLEAN_NOT_REQUESTED"}],
         "cleanPath":[{"x":0,"y":0,"update":1},{"x":1,"y":0,"update":0}],
         "obstacles":[{"x":2,"y":2}],
         "dirt":[{"x":-0.5,"y":-5.2,"type":"liquid","isUvScanOn":false}]}
        """;

    private static HistoryViewModel New(out TestHub.RouteHandler handler, string? detail = Detail)
    {
        var hub = TestHub.Create(out handler,
            ("persistent-map-metadata", "[" + TestHub.Map("1000000002", "Étage", isCurrent: true, ("10", "Cuisine", "kitchen")) + "]"),
            ("persistent-maps", TestHub.EmptyPersistentMap),
            ("clean-maps-data", detail ?? "{}"),
            ("clean-maps", OneClean));
        var maps = new MapCatalog(hub);
        _ = maps.LoadAsync();
        return new HistoryViewModel(hub, maps, new DisplaySettings());
    }

    [Fact]
    public async Task TheRoomListOfAPastCleanComesFromCleanStatusNotFromIsSelected()
    {
        var vm = New(out _);
        await vm.LoadAsync();

        await vm.FillDetailsAsync();

        // Everything the task actually touched or tried to, and nothing it was never asked to do.
        var row = Assert.Single(vm.History);
        Assert.Equal("Cuisine, Couloir, Chambre", row.Rooms);
        Assert.DoesNotContain("Pièce1", row.Rooms, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SelectingACleanShowsWhatWentWrongRoomByRoom()
    {
        var vm = New(out _);
        await vm.LoadAsync();

        vm.SelectedClean = vm.History[0];
        await WaitForAsync(() => vm.ResultText.Length > 0);

        // Completed rooms are left out: an ordinary clean should just read "Terminé".
        Assert.Equal("Couloir : Injoignable, Chambre : Non commencée", vm.ResultText);
        Assert.Equal("Étage", vm.MapName);
        Assert.Equal(2, vm.Scene.Path!.Count);
        Assert.Single(vm.Scene.Obstacles!);
        Assert.Single(vm.Scene.DirtSpots!);
    }

    [Fact]
    public async Task AnUneventfulCleanJustReadsDone()
    {
        var vm = New(out _, """
            {"cleanId":"clean-1","zones":[{"id":"10","name":"Cuisine","type":"kitchen","cleanStatus":"CLEAN_COMPLETE"}],"cleanPath":[]}
            """);
        await vm.LoadAsync();

        vm.SelectedClean = vm.History[0];
        await WaitForAsync(() => vm.ResultText.Length > 0);

        Assert.Equal("Terminé", vm.ResultText);
    }

    [Fact]
    public async Task TheDetailIsDownloadedOnceAndSharedWithTheBackgroundFill()
    {
        // Clicking a row while the background fill is fetching that same clean must join the
        // download in flight, not start a second one.
        var vm = New(out var handler);
        await vm.LoadAsync();

        var fill = vm.FillDetailsAsync();
        vm.SelectedClean = vm.History[0];
        await fill;
        await WaitForAsync(() => vm.ResultText.Length > 0);

        Assert.Equal(1, handler.Requested.Count(p => p.Contains("clean-maps-data", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task DeselectingClearsTheCardRatherThanLeavingTheLastCleanOnScreen()
    {
        var vm = New(out _);
        await vm.LoadAsync();
        vm.SelectedClean = vm.History[0];
        await WaitForAsync(() => vm.ResultText.Length > 0);

        vm.SelectedClean = null;

        Assert.Equal("", vm.ResultText);
        Assert.Equal("", vm.MapName);
        Assert.Null(vm.Scene.Path);
    }

    [Fact]
    public async Task ACleanOnAMapThatNoLongerExistsSaysSo()
    {
        var vm = New(out _, """{"cleanId":"clean-1","persistentMapId":"9999999999","zones":[],"cleanPath":[]}""");
        await vm.LoadAsync();

        vm.SelectedClean = vm.History[0];
        await WaitForAsync(() => vm.MapName.Length > 0);

        Assert.Equal("supprimée", vm.MapName);
    }

    [Fact]
    public async Task ARowKeepsItsSummaryFieldsFromTheListAlone()
    {
        var vm = New(out _);

        await vm.LoadAsync();

        var row = Assert.Single(vm.History);
        Assert.Equal("128 min", row.Duration);
        Assert.Equal("87.1 m²", row.Area);
        Assert.Equal("91 → 26 %", row.Battery);
        Assert.Equal("", row.Faults);
        Assert.Equal("…", row.Rooms); // until the detail arrives
    }

    /// <summary>Waits for a selection's fire-and-forget load to land, without pinning a duration.</summary>
    private static async Task WaitForAsync(Func<bool> done)
    {
        for (var i = 0; i < 100 && !done(); i++) await Task.Delay(10);
        Assert.True(done(), "the selected clean never finished loading");
    }
}
