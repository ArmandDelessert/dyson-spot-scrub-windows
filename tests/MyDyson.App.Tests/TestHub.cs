using System.Net.Http;
using System.Net;
using System.Text;
using System.Windows.Threading;
using MyDyson.App.Services;
using MyDyson.App.ViewModels;
using MyDyson.Core;

namespace MyDyson.App.Tests;

/// <summary>
/// Builds a <see cref="RobotHub"/> that talks to canned JSON instead of Dyson, so the tab view
/// models can be exercised without a robot, a broker or a window. There is no MQTT session, so
/// anything that sends a command is a no-op that logs "Robot non connecté" — which is exactly
/// what these tests want: they are about what the view models compute, not about the wire.
/// </summary>
internal static class TestHub
{
    /// <summary>Answers each request with the first canned body whose URL contains the given fragment.</summary>
    internal sealed class RouteHandler(params (string UrlContains, string Json)[] routes) : HttpMessageHandler
    {
        public List<string> Requested { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            Requested.Add(path);
            var route = routes.FirstOrDefault(r => path.Contains(r.UrlContains, StringComparison.Ordinal));
            return Task.FromResult(route.Json is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") }
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(route.Json, Encoding.UTF8, "application/json"),
                });
        }
    }

    internal static RobotHub Create(params (string UrlContains, string Json)[] routes) => Create(out _, routes);

    internal static RobotHub Create(out RouteHandler handler, params (string UrlContains, string Json)[] routes)
    {
        handler = new RouteHandler(routes);
        var api = new DysonCloudClient("CH", "fr-CH", http: new HttpClient(handler)) { BearerToken = "test-token" };
        var stored = new StoredSession("test@example.invalid", "CH", "fr-CH", null, "test-token", DateTimeOffset.UnixEpoch);
        // Dispatcher.CurrentDispatcher on the test thread: nothing here posts across threads, so
        // Post runs inline and assertions see the result straight away.
        return new RobotHub(RobotContext.FromLogin(api, stored), Dispatcher.CurrentDispatcher);
    }

    /// <summary>
    /// One map's metadata, with its rooms, as GET /persistent-map-metadata returns it. Serialised
    /// from the real records so the field names cannot drift apart from production (what those
    /// names are on the wire is pinned separately, by MapModelsTests in the Core suite).
    /// </summary>
    internal static string Map(string id, string name, bool isCurrent, params (string Id, string Name, string? Type)[] zones)
    {
        var rooms = zones.Select(z => new ZoneMetadata(
            z.Id, z.Name, z.Type, NameLocation: new Core.Point(0, 0), IsSelected: false, Order: 0, Area: 12.5,
            Settings: new ZoneSettings("auto", "vacuum", "low", 1, 1, true))).ToList();
        return System.Text.Json.JsonSerializer.Serialize(new MapMetadata(id, name, isCurrent, rooms, null));
    }

    /// <summary>An empty stored map, enough for the geometry fetch a zone load triggers.</summary>
    internal const string EmptyPersistentMap = """{"id":"1000000002","zones":[],"dockLocation":{"x":0,"y":0,"angle":0}}""";
}
