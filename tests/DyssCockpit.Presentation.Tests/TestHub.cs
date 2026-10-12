using System.Net.Http;
using System.Net;
using System.Text;
using DyssCockpit.Core;
using DyssCockpit.Presentation.Map;
using DyssCockpit.Presentation.Services;
using DyssCockpit.Presentation.ViewModels;

namespace DyssCockpit.Presentation.Tests;

/// <summary>
/// Builds a <see cref="RobotHub"/> that talks to canned JSON instead of Dyson, so the tab view
/// models can be exercised without a robot, a broker or a window. There is no MQTT session, so
/// anything that sends a command is a no-op that logs "Robot non connecté" — which is exactly
/// what these tests want: they are about what the view models compute, not about the wire. The
/// wire is <see cref="SentToTheRobotTests"/>' business, which puts a recording robot in
/// <see cref="RobotHub.Robot"/>.
/// </summary>
internal static class TestHub
{
    /// <summary>Answers each request with the first canned body whose URL contains the given fragment.</summary>
    internal sealed class RouteHandler(params (string UrlContains, string Json)[] routes) : HttpMessageHandler
    {
        public List<string> Requested { get; } = new();

        /// <summary>Bodies that are not JSON (a clean's snapshot), by a fragment of their URL; looked at first.</summary>
        public Dictionary<string, byte[]> Binary { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            Requested.Add(path);
            if (Binary.FirstOrDefault(b => path.Contains(b.Key, StringComparison.Ordinal)) is { Value: { } bytes })
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
            var route = routes.FirstOrDefault(r => path.Contains(r.UrlContains, StringComparison.Ordinal));
            return Task.FromResult(route.Json is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") }
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(route.Json, Encoding.UTF8, "application/json"),
                });
        }
    }

    internal static RobotHub Create(params (string UrlContains, string Json)[] routes) => Create(out _, new FakeDialogs(), routes);

    internal static RobotHub Create(out RouteHandler handler, params (string UrlContains, string Json)[] routes) =>
        Create(out handler, new FakeDialogs(), routes);

    /// <summary>A hub whose questions to the user <paramref name="dialogs"/> answers.</summary>
    internal static RobotHub Create(FakeDialogs dialogs, params (string UrlContains, string Json)[] routes) => Create(out _, dialogs, routes);

    internal static RobotHub Create(out RouteHandler handler, FakeDialogs dialogs, params (string UrlContains, string Json)[] routes)
    {
        handler = new RouteHandler(routes);
        return new RobotHub(Context(handler), new InlineDispatcher(), dialogs);
    }

    /// <summary>A logged-in account whose cloud answers from <paramref name="handler"/> (nothing at all when null), as the dashboard is given one.</summary>
    internal static RobotContext Context(HttpMessageHandler? handler = null, Microsoft.Extensions.Logging.ILoggerFactory? loggers = null)
    {
        var api = new DysonCloudClient("CH", "fr-CH", http: new HttpClient(handler ?? new RouteHandler())) { BearerToken = "test-token" };
        var stored = new StoredSession("test@example.invalid", "CH", "fr-CH", null, "test-token", DateTimeOffset.UnixEpoch);
        return RobotContext.FromLogin(api, stored, loggers);
    }

    /// <summary>The test thread as the UI thread: nothing here posts across threads, so Post runs inline and assertions see the result straight away.</summary>
    internal sealed class InlineDispatcher : IUiDispatcher
    {
        public bool CheckAccess() => true;
        public void Post(Action action) => action();
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

/// <summary>
/// Answers the view models' questions as a test sets it up. Unset, every question is declined —
/// what the view models did before anything answered them — so a test only sets what it is about.
/// </summary>
internal sealed class FakeDialogs : IDialogService
{
    public Func<string, string, bool> Confirm { get; set; } = (_, _) => false;
    public Func<string, string, string, string?> AskText { get; set; } = (_, _, _) => null;
    public Func<ManagedRoom, IReadOnlyList<RoomTypeOption>, (string Name, string? Type)?> AskRoomName { get; set; } = (_, _) => null;
    public Func<ScheduleEditorViewModel, bool> EditSchedule { get; set; } = _ => false;
    /// <summary>The warnings shown, title and text.</summary>
    public List<(string Title, string Message)> Alerts { get; } = [];

    public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(Confirm(title, message));

    public Task AlertAsync(string title, string message)
    {
        Alerts.Add((title, message));
        return Task.CompletedTask;
    }

    public Task<string?> AskTextAsync(string title, string prompt, string initial) => Task.FromResult(AskText(title, prompt, initial));

    public Task<(string Name, string? Type)?> AskRoomNameAsync(ManagedRoom room, IReadOnlyList<RoomTypeOption> types) => Task.FromResult(AskRoomName(room, types));

    public Task<bool> EditScheduleAsync(ScheduleEditorViewModel editor) => Task.FromResult(EditSchedule(editor));

    /// <summary>The file names offered for a map export; the answer is no file, as when the user cancels.</summary>
    public Func<MapScene, string, string?> SaveMapImage { get; set; } = (_, _) => null;

    public Task<string?> SaveMapImageAsync(MapScene scene, string suggestedFileName) => Task.FromResult(SaveMapImage(scene, suggestedFileName));
}
