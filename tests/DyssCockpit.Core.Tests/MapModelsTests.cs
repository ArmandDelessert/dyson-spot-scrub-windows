using System.Net;
using System.Text;
using DyssCockpit.Core;

namespace DyssCockpit.Core.Tests;

/// <summary>
/// The REST map shapes, parsed through the real client so the options it uses are what gets
/// exercised. Payloads are anonymised cut-downs of actual responses (see docs/protocole.md); the
/// point is to pin the field names and shapes, since a silent rename would not fail to compile —
/// it would just make the map render empty.
/// </summary>
public class MapModelsTests
{
    private static DysonCloudClient ClientReturning(string json) =>
        new("CH", "fr-CH", http: new HttpClient(new StubHandler(json))) { BearerToken = "test-token" };

    private sealed class StubHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
    }

    [Fact]
    public async Task PersistentMapCarriesZonesGeometryDockAndFurniture()
    {
        using var api = ClientReturning("""
            {"id":"1000000002","orientation":0,
             "dimensions":{"width":320,"height":420,"resolution":0.05,"offsetX":-8.2,"offsetY":-10.4},
             "dockLocation":{"x":-0.67,"y":-0.02,"angle":1.57},
             "zones":[{"id":"10","name":"Cuisine","type":"kitchen","area":29.7,
                       "nameLocation":{"x":0.5,"y":1.25},
                       "visited":[{"x":0.1,"y":0.2},{"x":0.3,"y":0.4}],
                       "presentation":[{"start":{"x":0,"y":0},"end":{"x":1,"y":0},"type":1}],
                       "cleanStatus":"CLEAN_COMPLETE",
                       "isSelected":true,
                       "settings":{"cleaningStrategy":"auto","cleanType":"vacuumAndMop","waterLevel":"low","mopPasses":1,"dryPasses":1,"isUvScanOn":true}}],
             "furniture":[{"id":"f1","type":"sofa","points":[{"x":1,"y":1},{"x":2,"y":1},{"x":2,"y":2}]}],
             "restrictions":[],
             "somethingDysonAddedLater":42}
            """);

        var map = await api.GetPersistentMapAsync("SERIAL", "1000000002", TestContext.Current.CancellationToken);

        Assert.Equal("1000000002", map.Id);
        Assert.Equal((320, 420, 0.05), (map.Dimensions!.Width, map.Dimensions.Height, map.Dimensions.Resolution));
        Assert.Equal((-8.2, -10.4), (map.Dimensions.OffsetX, map.Dimensions.OffsetY));
        Assert.Equal((-0.67, -0.02), (map.DockLocation!.X, map.DockLocation.Y));

        var zone = Assert.Single(map.Zones!);
        Assert.Equal(("10", "Cuisine", "kitchen"), (zone.Id, zone.Name, zone.Type));
        Assert.Equal(29.7, zone.Area);
        Assert.Equal(1.25, zone.NameLocation!.Y);
        Assert.Equal(2, zone.Visited!.Count);
        Assert.Equal(1, Assert.Single(zone.Presentation!).Type);
        Assert.Equal("CLEAN_COMPLETE", zone.CleanStatus);
        Assert.Equal("vacuumAndMop", zone.Settings!.CleanType);
        Assert.Equal(3, Assert.Single(map.Furniture!).Points!.Count);
        // Unknown members are kept rather than dropped, so a capture can show what was ignored.
        Assert.True(map.Extra!.ContainsKey("somethingDysonAddedLater"));
    }

    [Fact]
    public async Task LiveMapCarriesThePathWithItsWorkingFlagPlusObstaclesAndDirt()
    {
        using var api = ClientReturning("""
            {"robotLocation":{"id":613,"x":0.94,"y":2.21,"angle":-2.62,"update":1},
             "cleanPath":[{"x":-0.77,"y":3.23,"update":1},{"x":-0.76,"y":3.11,"update":0}],
             "obstacles":[{"x":1.5,"y":-2.25}],
             "dirt":[{"x":-0.53,"y":-5.25,"type":"liquid","isUvScanOn":false}],
             "hazardZones":[],"groutLines":[],"swingDoors":[]}
            """);

        var live = await api.GetLiveCleaningMapAsync("SERIAL", TestContext.Current.CancellationToken);

        Assert.Equal(613, live.RobotLocation!.Id);
        Assert.Equal(2, live.CleanPath!.Count);
        // update 1 means the robot was actually working there, 0 that it was only repositioning;
        // the map colours the trail by this.
        Assert.Equal(1, live.CleanPath[0].Update);
        Assert.Equal(0, live.CleanPath[1].Update);
        var obstacle = Assert.Single(live.Obstacles!);
        Assert.Equal((1.5, -2.25), (obstacle.X, obstacle.Y));
        var dirt = Assert.Single(live.Dirt!);
        Assert.Equal(("liquid", false), (dirt.Type, dirt.IsUvScanOn));
        // Always empty so far, so their shape is unknown and they stay raw.
        Assert.Empty(live.HazardZones!);
    }

    [Fact]
    public async Task APathPointWithoutAnUpdateFlagIsNotReadAsWorking()
    {
        // Older or partial payloads omit "update"; null must not be taken for 1, which would paint
        // the whole trail as if the robot had been cleaning all along.
        using var api = ClientReturning("""{"cleanPath":[{"x":0,"y":0}]}""");

        var live = await api.GetLiveCleaningMapAsync("SERIAL", TestContext.Current.CancellationToken);

        Assert.Null(Assert.Single(live.CleanPath!).Update);
    }

    [Fact]
    public async Task CleanHistoryBatteriesArriveAsDecimals()
    {
        // Batteries arrive as 91.0 rather than 91, which an int property would refuse outright.
        // The list is wrapped in a "data" envelope, unlike the map endpoints, times are Unix
        // seconds, and the duration is in minutes where the classic dialect uses seconds.
        using var api = ClientReturning("""
            {"data":[{"cleanId":"abc","persistentMapId":"1000000002","isSpotClean":false,
              "startTime":1789916722,"endTime":1789924402,
              "cleanDuration":128,"areaCleaned":87.1,
              "startBattery":91.0,"endBattery":26.0,"startMethod":1,
              "firmwareVersion":"RB05PR.01.000.0436","faults":[],
              "downloadUrl":"https://example.invalid/blob"}]}
            """);

        var clean = Assert.Single(await api.GetCleanHistoryAsync("SERIAL", TestContext.Current.CancellationToken));

        Assert.Equal("abc", clean.CleanId);
        Assert.Equal(128, clean.CleanDurationMinutes);
        Assert.Equal(87.1, clean.AreaCleanedSquareMetres);
        Assert.Equal(91.0, clean.StartBattery);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1789916722), clean.Start);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1789924402), clean.End);
        Assert.False(clean.IsSpotClean);
    }

    [Fact]
    public async Task MappingMapDecodesIntoAGridWhoseCellsMatchTheZoneIds()
    {
        using var api = ClientReturning("""
            {"dimensions":{"width":2,"height":2,"resolution":0.5,"offsetX":0,"offsetY":0},
             "mapData":[0,10,11,255]}
            """);

        var grid = MapGrid.From(await api.GetMappingMapAsync("SERIAL", TestContext.Current.CancellationToken))!;

        Assert.Equal((2, 2), (grid.Width, grid.Height));
        Assert.Equal(MapGrid.Unknown, grid[0, 0]);
        Assert.Equal(10, grid[1, 0]);
        Assert.Equal(11, grid[0, 1]);
        Assert.True(grid.IsObstacle(1, 1));
    }
}

public class AuthenticatedEndpointTests
{
    [Fact]
    public async Task AnAuthenticatedCallWithoutATokenFailsBeforeReachingTheNetwork()
    {
        // Better a clear "log in first" than an HTTP 401 from a request that should never have
        // been sent; the app turns this into a return to the login window.
        using var api = new DysonCloudClient("CH", "fr-CH", http: new HttpClient(new ThrowingHandler()));

        await Assert.ThrowsAsync<DysonAuthException>(() => api.GetMapMetadataAsync("SERIAL", TestContext.Current.CancellationToken));
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new InvalidOperationException("no request should be sent");
    }
}
