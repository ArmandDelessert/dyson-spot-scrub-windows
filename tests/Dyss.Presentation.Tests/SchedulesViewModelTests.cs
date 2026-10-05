using System.Globalization;
using System.Text.Json.Nodes;
using Dyss.Presentation.ViewModels;
using Dyss.Core;

namespace Dyss.Presentation.Tests;

/// <summary>
/// The Horaires tab and its editor, without a robot: what the list shows of the cloud's schedules
/// for the active map, when it reads them again, what the editor lets through, and that nothing is
/// taken as done when it could not be sent.
/// </summary>
public class SchedulesViewModelTests
{
    private const string ActiveId = "1000000002";
    private const string OtherId = "1000000003";

    /// <summary>One cloud event: its rooms as (id, selected, order); the settings are the phone's defaults.</summary>
    private static JsonObject Event(long groupId, int day, string time, bool enabled, string mapId, params (string Id, bool Selected, int Order)[] zones)
    {
        var rooms = new JsonArray();
        foreach (var z in zones)
            rooms.Add(new JsonObject
            {
                ["id"] = z.Id, ["name"] = "x", ["type"] = "", ["isSelected"] = z.Selected, ["order"] = z.Order,
                ["settings"] = new JsonObject { ["cleanType"] = "vacuum", ["cleaningStrategy"] = "auto", ["waterLevel"] = "low", ["mopPasses"] = 1 },
            });
        return new JsonObject
        {
            ["groupId"] = groupId, ["days"] = new JsonArray(day), ["startTime"] = time, ["weeklyRepeat"] = true, ["enabled"] = enabled,
            ["settings"] = new JsonObject { ["persistentMapId"] = mapId, ["zones"] = rooms },
        };
    }

    private static string Events(params JsonObject[] events) =>
        new JsonObject { ["enabled"] = true, ["serial"] = "SERIAL", ["events"] = new JsonArray([.. events]) }.ToJsonString();

    private static async Task<(SchedulesViewModel Vm, RobotHub Hub, MapCatalog Maps)> NewAsync(string? events = null) =>
        await NewAsync(out _, events);

    private static Task<(SchedulesViewModel Vm, RobotHub Hub, MapCatalog Maps)> NewAsync(out TestHub.RouteHandler handler, string? events = null)
    {
        var hub = TestHub.Create(out handler,
            ("persistent-map-metadata", "["
                + TestHub.Map(ActiveId, "Étage", isCurrent: true, ("10", "Cuisine", "kitchen"), ("11", "Bureau", "office"), ("12", "Salon", "livingRoom")) + ","
                + TestHub.Map(OtherId, "Rez", isCurrent: false, ("20", "Salon", "livingRoom")) + "]"),
            ("unifiedscheduler", events ?? Events()));
        var maps = new MapCatalog(hub);
        var vm = new SchedulesViewModel(hub, maps, () => []) { ReloadDelay = TimeSpan.Zero };
        return LoadedAsync(vm, hub, maps);
    }

    private static async Task<(SchedulesViewModel, RobotHub, MapCatalog)> LoadedAsync(SchedulesViewModel vm, RobotHub hub, MapCatalog maps)
    {
        await maps.LoadAsync();
        await vm.LoadAsync();
        return (vm, hub, maps);
    }

    private static CleaningSchedule Schedule(long id, string mapId, int hour, ScheduleDays days = ScheduleDays.Monday, bool enabled = true, params int[] rooms) =>
        new(id, long.Parse(mapId, CultureInfo.InvariantCulture), enabled, days, hour, 0,
            (rooms.Length == 0 ? [10] : rooms).Select(r => new ScheduledRoom(r, new RoomSettings(CleanType.Vacuum))).ToList());

    // ---- The list ------------------------------------------------------------------------

    [Fact]
    public async Task TheCloudsSchedulesOfTheActiveMapAreListedByTime()
    {
        var (vm, _, _) = await NewAsync(Events(
            Event(2, 0, "18:00", true, ActiveId, ("11", true, 0), ("10", true, 1), ("12", false, 2)),
            Event(1, 1, "09:30", false, ActiveId, ("10", true, 0)),
            // Another map's, as the cloud may still list it just after a switch: left out.
            Event(3, 2, "08:00", true, OtherId, ("20", true, 0))));

        Assert.Equal("Carte active : Étage", vm.Heading);
        Assert.Equal(["09:30", "18:00"], vm.Items.Select(i => i.TimeText));
        Assert.Equal(["Le lundi", "Le dimanche"], vm.Items.Select(i => i.DaysText));
        Assert.Equal("Bureau, Cuisine", vm.Items[1].RoomsText);
        Assert.False(vm.Items[0].Enabled);
        Assert.Equal("", vm.EmptyText);
    }

    [Fact]
    public async Task TheListFollowsTheWeekThenTheTime()
    {
        var (vm, _, _) = await NewAsync(Events(
            Event(1, 2, "08:00", true, ActiveId, ("10", true, 0)),    // Tuesday
            Event(2, 1, "20:00", true, ActiveId, ("10", true, 0)),    // Monday
            Event(3, 1, "07:00", true, ActiveId, ("10", true, 0)),    // Monday
            Event(4, 0, "06:00", true, ActiveId, ("10", true, 0))));  // Sunday, last of the week

        Assert.Equal(["07:00", "20:00", "08:00", "06:00"], vm.Items.Select(i => i.TimeText));
    }

    [Fact]
    public async Task AnEmptyListSaysSo()
    {
        var (vm, _, _) = await NewAsync();

        Assert.Empty(vm.Items);
        Assert.Equal("Aucun horaire pour cette carte.", vm.EmptyText);
    }

    [Fact]
    public async Task TheRobotReportingAChangeReadsTheCloudAgain()
    {
        var (vm, _, _) = await NewAsync(out var handler);
        vm.ApplyJdm(Jdm("""{"order_total":{"total":0,"enable":0}}"""));   // the first report is only noted
        var before = handler.Requested.Count(p => p.Contains("unifiedscheduler", StringComparison.Ordinal));

        vm.ApplyJdm(Jdm("""{"order_total":{"total":1,"enable":1}}"""));
        for (var i = 0; i < 100 && handler.Requested.Count(p => p.Contains("unifiedscheduler", StringComparison.Ordinal)) == before; i++) await Task.Delay(10, TestContext.Current.CancellationToken);

        Assert.Equal(before + 1, handler.Requested.Count(p => p.Contains("unifiedscheduler", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task TwoSchedulesTooCloseAreFlagged()
    {
        // Three rooms of 12.5 m² at the default 1.5 min/m² is about 57 minutes.
        var (vm, _, _) = await NewAsync(Events(
            Event(1, 1, "09:00", true, ActiveId, ("10", true, 0), ("11", true, 1), ("12", true, 2)),
            Event(2, 1, "09:30", true, ActiveId, ("10", true, 0))));

        Assert.Equal("", vm.Items[0].OverlapText);
        Assert.StartsWith("Risque de ne pas démarrer : l'horaire de 09:00", vm.Items[1].OverlapText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NothingCanBeChangedWithoutTheRobot()
    {
        var (vm, hub, _) = await NewAsync(Events(Event(1, 1, "09:00", true, ActiveId, ("10", true, 0))));

        Assert.False(vm.NewCommand.CanExecute(null));
        Assert.False(vm.ToggleCommand.CanExecute(vm.Items[0]));

        hub.Connected = true;
        Assert.True(vm.NewCommand.CanExecute(null));
        Assert.True(vm.ToggleCommand.CanExecute(vm.Items[0]));
        Assert.False(vm.ToggleCommand.CanExecute(null));
    }

    [Fact]
    public async Task WithoutAnActiveMapThereIsNothingToScheduleFor()
    {
        var hub = TestHub.Create(("persistent-map-metadata", "[" + TestHub.Map(OtherId, "Rez", isCurrent: false, ("20", "Salon", "livingRoom")) + "]"));
        var maps = new MapCatalog(hub);
        var vm = new SchedulesViewModel(hub, maps, () => []);
        await maps.LoadAsync();
        hub.Connected = true;

        Assert.Equal("Aucune carte active", vm.Heading);
        Assert.False(vm.NewCommand.CanExecute(null));
    }

    [Fact]
    public async Task AScheduleThatCouldNotBeSentIsNotListed()
    {
        var (vm, hub, _) = await NewAsync();
        hub.Connected = true;   // the flag says so, but there is no session to send with
        ((FakeDialogs)hub.Dialogs).EditSchedule = editor =>
        {
            editor.Days[0].IsChecked = true;
            editor.Rooms[0].Selected = true;
            return true;
        };

        await vm.NewCommand.ExecuteAsync(null);

        Assert.Equal("Robot non connecté.", vm.Status);
        Assert.Empty(vm.Items);
    }

    [Fact]
    public async Task ACancelledEditorSendsNothing()
    {
        var (vm, hub, _) = await NewAsync(Events(Event(1, 1, "09:00", true, ActiveId, ("10", true, 0))));
        hub.Connected = true;
        ((FakeDialogs)hub.Dialogs).EditSchedule = _ => false;

        await vm.EditCommand.ExecuteAsync(vm.Items[0]);

        Assert.Equal("", vm.Status);
    }

    private static JdmProperties Jdm(string json)
    {
        var jdm = new JdmProperties();
        jdm.Merge((JsonObject)JsonNode.Parse(json)!);
        return jdm;
    }

    // ---- The editor ------------------------------------------------------------------------

    private static async Task<ScheduleEditorViewModel> EditorAsync(CleaningSchedule? existing = null, params CleaningSchedule[] others)
    {
        var (_, _, maps) = await NewAsync();
        return new ScheduleEditorViewModel(maps.Find(ActiveId)!, existing, others, 1.5);
    }

    [Fact]
    public async Task SavingNeedsADayAndARoomAndSaysWhichIsMissing()
    {
        var editor = await EditorAsync();

        Assert.False(editor.CanSave);
        Assert.Equal("Choisissez au moins un jour et une pièce.", editor.Missing);

        editor.Days[3].IsChecked = true;
        Assert.Equal("Choisissez au moins une pièce.", editor.Missing);

        Room(editor, "11").Selected = true;
        Assert.True(editor.CanSave);
        Assert.Equal("", editor.Missing);
    }

    [Fact]
    public async Task RoomsAreCleanedInTheOrderTheyWereTickedWithTheirOwnSettings()
    {
        var editor = await EditorAsync();
        editor.Days[0].IsChecked = true;
        editor.Days[6].IsChecked = true;
        editor.Hour = 12;
        editor.Minute = 30;

        Room(editor, "12").Selected = true;
        Room(editor, "10").Selected = true;
        Room(editor, "11").Selected = true;
        Room(editor, "10").SelectedCleanType = ZoneItem.CleanTypeOptions.Single(o => o.Value == CleanType.Mop);
        Room(editor, "10").SelectedWaterLevel = ZoneItem.WaterLevelOptions.Single(o => o.Value == WaterLevel.High);
        // Unticking the first closes the gap: Cuisine becomes 1, Bureau 2.
        Room(editor, "12").Selected = false;

        var s = editor.Build(42);

        Assert.Equal((42L, 1000000002L, true), (s.Id, s.MapId, s.Enabled));
        Assert.Equal(ScheduleDays.Monday | ScheduleDays.Sunday, s.Days);
        Assert.Equal((12, 30), (s.Hour, s.Minute));
        Assert.Equal([10, 11], s.Rooms.Select(r => r.ZoneId));
        Assert.Equal(new RoomSettings(CleanType.Mop, CleaningStrategy.Auto, WaterLevel.High, 1), s.Rooms[0].Settings);
        Assert.Equal([1, 2], new[] { Room(editor, "10").Order, Room(editor, "11").Order });
    }

    [Fact]
    public async Task AnExistingScheduleOpensAsItWasSaved()
    {
        var existing = new CleaningSchedule(7, 1000000002, false, ScheduleDays.Tuesday | ScheduleDays.Friday, 7, 45,
        [
            new(11, new RoomSettings(CleanType.VacuumThenMop, CleaningStrategy.Boost, WaterLevel.Medium, 2)),
            new(10, new RoomSettings(CleanType.Vacuum)),
        ]);

        var editor = await EditorAsync(existing);

        Assert.Equal("Modifier l'horaire", editor.Title);
        Assert.Equal((7, 45), (editor.Hour, editor.Minute));
        Assert.Equal(["mardi", "vendredi"], editor.Days.Where(d => d.IsChecked).Select(d => d.Name));
        Assert.Equal(1, Room(editor, "11").Order);
        Assert.Equal(2, Room(editor, "10").Order);
        Assert.Equal(existing.Rooms[0].Settings, Room(editor, "11").Settings);
        // Unchanged, it builds back the very same schedule, still off.
        var rebuilt = editor.Build(7);
        Assert.Equal(existing with { Rooms = [] }, rebuilt with { Rooms = [] });
        Assert.Equal(existing.Rooms, rebuilt.Rooms);
    }

    [Fact]
    public async Task TheEditorWarnsWhenAnotherScheduleWouldStillBeRunning()
    {
        var other = Schedule(1, ActiveId, 10, rooms: [10, 11, 12]);   // about 57 minutes from 10:00
        var editor = await EditorAsync(null, other);
        editor.Days[0].IsChecked = true;
        Room(editor, "11").Selected = true;

        editor.Hour = 10;
        editor.Minute = 29;
        Assert.StartsWith("Chevauche l'horaire de 10:00 (le lundi)", editor.OverlapWarning, StringComparison.Ordinal);
        Assert.Equal("Durée estimée : environ 19 min, d'après les nettoyages passés.", editor.EstimateText);

        editor.Hour = 11;
        Assert.Equal("", editor.OverlapWarning);
    }

    [Fact]
    public async Task TheSaveButtonsTipOnlyExistsWhileSomethingIsMissing()
    {
        var editor = await EditorAsync();
        Assert.Equal("Choisissez au moins un jour et une pièce.", editor.MissingTip);

        editor.Days[0].IsChecked = true;
        Room(editor, "10").Selected = true;

        // Null rather than "": an empty tooltip still shows, as an empty bubble.
        Assert.Null(editor.MissingTip);
    }

    [Fact]
    public async Task AllRoomsCanBeUntickedAtOnce()
    {
        var editor = await EditorAsync();
        Assert.False(editor.ClearRoomsCommand.CanExecute(null));
        Room(editor, "12").Selected = true;
        Room(editor, "10").Selected = true;
        Assert.True(editor.ClearRoomsCommand.CanExecute(null));

        editor.ClearRoomsCommand.Execute(null);

        Assert.All(editor.Rooms, r => Assert.False(r.Selected));
        Assert.All(editor.Rooms, r => Assert.Equal(0, r.Order));
        Assert.False(editor.ClearRoomsCommand.CanExecute(null));
        // Ticking again starts the numbering over.
        Room(editor, "11").Selected = true;
        Assert.Equal(1, Room(editor, "11").Order);
    }

    private static ZoneItem Room(ScheduleEditorViewModel editor, string id) => editor.Rooms.Single(r => r.Id == id);

}
