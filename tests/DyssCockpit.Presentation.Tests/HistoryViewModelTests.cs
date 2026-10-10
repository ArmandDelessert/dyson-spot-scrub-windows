using System.Text.Json;
using DyssCockpit.Core;
using DyssCockpit.Presentation.Services;
using DyssCockpit.Presentation.ViewModels;

namespace DyssCockpit.Presentation.Tests;

/// <summary>
/// The Historique tab. The interesting part is which rooms a past clean covered: the only honest
/// source is each zone's cleanStatus in that clean's own detail (see docs/protocole.md), and the
/// detail is several hundred KB, so it is fetched once and shared.
/// </summary>
public sealed class HistoryViewModelTests : IDisposable
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

    /// <summary>Where the tests that keep cleans keep them: this class's own, never the user's.</summary>
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private readonly string _kept = Path.Combine(Path.GetTempPath(), $"dyss-kept-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_kept)) Directory.Delete(_kept, recursive: true);
    }

    private HistoryViewModel New(out TestHub.RouteHandler handler, string? detail = Detail, AppSettings? settings = null, string? history = OneClean)
    {
        var routes = new List<(string, string)>
        {
            ("persistent-map-metadata", "[" + TestHub.Map("1000000002", "Étage", isCurrent: true, ("10", "Cuisine", "kitchen")) + "]"),
            ("persistent-maps", TestHub.EmptyPersistentMap),
            ("clean-maps-data", detail ?? "{}"),
        };
        // No history route: the cloud answers "not found", as it would to a computer that is offline.
        if (history is not null) routes.Add(("clean-maps", history));
        var hub = TestHub.Create(out handler, [.. routes]);
        var maps = new MapCatalog(hub);
        _ = maps.LoadAsync();
        return new HistoryViewModel(hub, maps, settings ?? new AppSettings(), new CleanArchive(_kept));
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

    private const string OlderClean = """
        {"cleanId":"clean-0","persistentMapId":"1000000002","startTime":1750000000,"endTime":1750000600,"cleanDuration":10,"areaCleaned":6.4,"faults":[]}
        """;

    private const string KeptDetail = """
        {"cleanId":"clean-0","persistentMapId":"1000000002","zones":[{"id":"10","name":"Cuisine","type":"kitchen","cleanStatus":"CLEAN_COMPLETE"}],
         "cleanPath":[{"x":0,"y":0,"update":1}],"dirt":[{"x":3.7,"y":3.9,"type":"solid","isUvScanOn":false}]}
        """;

    [Fact]
    public async Task NothingIsKeptOnDiskUnlessTheSettingAsksForIt()
    {
        var vm = New(out _);
        await vm.LoadAsync();

        await vm.FillDetailsAsync();

        Assert.False(Directory.Exists(_kept));
    }

    [Fact]
    public async Task WithTheSettingOnEachCleanIsKeptAsItsDetailIsDownloaded()
    {
        var vm = New(out var handler, settings: new AppSettings { ArchiveCleans = true });
        await vm.LoadAsync();

        await vm.FillDetailsAsync();

        var archive = new CleanArchive(_kept);
        Assert.True(archive.Has("clean-1"));
        Assert.Equal("liquid", Assert.Single(archive.LoadDetail("clean-1")!.Dirt!).Type);

        // Once kept, a fresh start reads it from disk and does not download it again.
        var again = New(out var secondHandler, settings: new AppSettings { ArchiveCleans = true });
        await again.LoadAsync();
        await again.FillDetailsAsync();
        Assert.Equal("Cuisine, Couloir, Chambre", Assert.Single(again.History).Rooms);
        Assert.Equal(1, handler.Requested.Count(p => p.Contains("clean-maps-data", StringComparison.Ordinal)));
        Assert.DoesNotContain(secondHandler.Requested, p => p.Contains("clean-maps-data", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ACleanTheCloudNoLongerListsStaysInTheListAndShowsFromDisk()
    {
        var archive = new CleanArchive(_kept);
        archive.Save(JsonSerializer.Deserialize<CleanSummary>(OlderClean, Web)!,
            JsonSerializer.Deserialize<CleanDetail>(KeptDetail, Web)!);
        var vm = New(out var handler, settings: new AppSettings { ArchiveCleans = true });

        await vm.LoadAsync();

        // The cloud's clean first, then the one only the disk has, by date.
        Assert.Equal(["clean-1", "clean-0"], vm.History.Select(c => c.Summary.CleanId));
        vm.SelectedClean = vm.History[1];
        await WaitForAsync(() => vm.ResultText.Length > 0);
        Assert.Equal("Terminé", vm.ResultText);
        Assert.Equal("solid", Assert.Single(vm.Scene.DirtSpots!).Type);
        Assert.DoesNotContain(handler.Requested, p => p.Contains("clean-maps-data", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WithoutTheSettingTheListIsTheCloudsAlone()
    {
        new CleanArchive(_kept).Save(JsonSerializer.Deserialize<CleanSummary>(OlderClean, Web)!,
            JsonSerializer.Deserialize<CleanDetail>(KeptDetail, Web)!);
        var vm = New(out _);

        await vm.LoadAsync();

        Assert.Equal(["clean-1"], vm.History.Select(c => c.Summary.CleanId));
    }

    [Fact]
    public async Task OfflineTheKeptCleansAreStillThereToLookAt()
    {
        new CleanArchive(_kept).Save(JsonSerializer.Deserialize<CleanSummary>(OlderClean, Web)!,
            JsonSerializer.Deserialize<CleanDetail>(KeptDetail, Web)!);
        var vm = New(out _, settings: new AppSettings { ArchiveCleans = true }, history: null);

        await vm.LoadAsync();

        Assert.Equal(["clean-0"], vm.History.Select(c => c.Summary.CleanId));
    }

    [Fact]
    public async Task SwitchingTheSettingOnKeepsWhatTheListAlreadyShows()
    {
        var settings = new AppSettings();
        var vm = New(out _, settings: settings);
        await vm.LoadAsync();
        await vm.FillDetailsAsync();
        Assert.False(new CleanArchive(_kept).Has("clean-1"));

        settings.ArchiveCleans = true;
        await WaitForAsync(() => new CleanArchive(_kept).Has("clean-1"));

        Assert.True(new CleanArchive(_kept).Has("clean-1"));
    }

    [Fact]
    public async Task ShorteningTheRetentionClearsTheCleansNowTooOld()
    {
        var archive = new CleanArchive(_kept);
        archive.Save(JsonSerializer.Deserialize<CleanSummary>(OlderClean, Web)!,
            JsonSerializer.Deserialize<CleanDetail>(KeptDetail, Web)!);   // June 2025
        var settings = new AppSettings { ArchiveCleans = true, CleanArchiveRetentionDays = 0 };
        using var vm = New(out _, settings: settings);
        await vm.LoadAsync();
        Assert.True(archive.Has("clean-0"));

        settings.CleanArchiveRetentionDays = 30;   // clean-0 is far older than that, whatever today is

        Assert.False(archive.Has("clean-0"));
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
