using System.IO;
using System.Text.Json.Nodes;
using MyDyson.App.Services;
using MyDyson.App.ViewModels;
using MyDyson.Core;

namespace MyDyson.App.Tests;

/// <summary>
/// The Horaires tab and its editor, without a robot: what the list shows for the active map, what
/// the robot's count says about schedules made elsewhere, what the editor lets through, and that a
/// refused or unsent change leaves the stored list alone.
/// </summary>
public class SchedulesViewModelTests
{
    private const string ActiveId = "1000000002";
    private const string OtherId = "1000000003";

    private static async Task<(SchedulesViewModel Vm, RobotHub Hub, ScheduleStore Store, MapCatalog Maps)> NewAsync(params CleaningSchedule[] stored)
    {
        var hub = TestHub.Create(
            ("persistent-map-metadata", "["
                + TestHub.Map(ActiveId, "Étage", isCurrent: true, ("10", "Cuisine", "kitchen"), ("11", "Bureau", "office"), ("12", "Salon", "livingRoom")) + ","
                + TestHub.Map(OtherId, "Rez", isCurrent: false, ("20", "Salon", "livingRoom")) + "]"));
        var store = new ScheduleStore();
        foreach (var s in stored) store.Save(hub.Serial, s);
        var maps = new MapCatalog(hub);
        var vm = new SchedulesViewModel(hub, maps, store, () => []);
        await maps.LoadAsync();
        return (vm, hub, store, maps);
    }

    private static CleaningSchedule Schedule(long id, string mapId, int hour, ScheduleDays days = ScheduleDays.Monday, bool enabled = true, params int[] rooms) =>
        new(id, long.Parse(mapId, System.Globalization.CultureInfo.InvariantCulture), enabled, days, hour, 0,
            (rooms.Length == 0 ? [10] : rooms).Select(r => new ScheduledRoom(r, new RoomSettings(CleanType.Vacuum))).ToList());

    // ---- The list ------------------------------------------------------------------------

    [Fact]
    public async Task OnlyTheActiveMapsSchedulesAreListedByTimeAndTheOthersAreCounted()
    {
        var (vm, _, _, _) = await NewAsync(
            Schedule(1, ActiveId, 18, ScheduleDays.Weekend, rooms: [11, 10]),
            Schedule(2, ActiveId, 9, ScheduleDays.All),
            Schedule(3, OtherId, 10));

        Assert.Equal("Carte active : Étage", vm.Heading);
        Assert.Equal(["09:00", "18:00"], vm.Items.Select(i => i.TimeText));
        Assert.Equal("Tous les jours", vm.Items[0].DaysText);
        Assert.Equal("Bureau, Cuisine", vm.Items[1].RoomsText);
        Assert.StartsWith("1 autre horaire créé ici pour d'autres cartes", vm.OtherMapsText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheRobotsCountRevealsSchedulesMadeOnThePhone()
    {
        var (vm, _, _, _) = await NewAsync(Schedule(1, ActiveId, 9));

        vm.ApplyJdm(Jdm("""{"order_total":{"total":3,"enable":2}}"""));
        Assert.Equal("Le robot a 3 horaires pour cette carte, dont 2 activés.", vm.RobotCountText);
        Assert.StartsWith("2 de ces horaires ont été créés depuis l'application mobile", vm.SyncNotice, StringComparison.Ordinal);

        vm.ApplyJdm(Jdm("""{"order_total":{"total":1,"enable":1}}"""));
        Assert.Equal("", vm.SyncNotice);

        vm.ApplyJdm(Jdm("""{"order_total":{"total":0,"enable":0}}"""));
        Assert.StartsWith("Le robot compte moins d'horaires", vm.SyncNotice, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TwoSchedulesTooCloseAreFlagged()
    {
        // Three rooms of 12.5 m² at the default 1.5 min/m² is about 57 minutes.
        var (vm, _, _, _) = await NewAsync(
            Schedule(1, ActiveId, 9, rooms: [10, 11, 12]),
            Schedule(2, ActiveId, 9, rooms: [10]) with { Minute = 30 });

        Assert.Equal("", vm.Items[0].OverlapText);
        Assert.StartsWith("Risque de ne pas démarrer : l'horaire de 09:00", vm.Items[1].OverlapText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NothingCanBeChangedWithoutTheRobot()
    {
        var (vm, hub, _, _) = await NewAsync(Schedule(1, ActiveId, 9));

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
        var vm = new SchedulesViewModel(hub, maps, new ScheduleStore(), () => []);
        await maps.LoadAsync();
        hub.Connected = true;

        Assert.Equal("Aucune carte active", vm.Heading);
        Assert.False(vm.NewCommand.CanExecute(null));
    }

    [Fact]
    public async Task AScheduleThatCouldNotBeSentIsNotStored()
    {
        var (vm, hub, store, _) = await NewAsync();
        hub.Connected = true;   // the flag says so, but there is no session to send with
        vm.EditSchedule = editor =>
        {
            editor.Days[0].IsChecked = true;
            editor.Rooms[0].Selected = true;
            return true;
        };

        await vm.NewCommand.ExecuteAsync(null);

        Assert.Equal("Robot non connecté.", vm.Status);
        Assert.Empty(store.For(hub.Serial));
        Assert.Empty(vm.Items);
    }

    [Fact]
    public async Task ACancelledEditorSendsNothing()
    {
        var (vm, hub, _, _) = await NewAsync(Schedule(1, ActiveId, 9));
        hub.Connected = true;
        vm.EditSchedule = _ => false;

        await vm.EditCommand.ExecuteAsync(vm.Items[0]);

        Assert.Equal("", vm.Status);
    }

    // ---- The editor ------------------------------------------------------------------------

    private static async Task<ScheduleEditorViewModel> EditorAsync(CleaningSchedule? existing = null, params CleaningSchedule[] others)
    {
        var (_, _, _, maps) = await NewAsync();
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

    // ---- The stored list -----------------------------------------------------------------------

    [Fact]
    public void TheStoredListSurvivesARestartAndIsKeptPerRobot()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mydyson-schedules-{Guid.NewGuid():N}.json");
        try
        {
            var store = ScheduleStore.Load(path);
            store.Save("SERIAL-A", Schedule(1, ActiveId, 9));
            store.Save("SERIAL-A", Schedule(1, ActiveId, 10));   // same id: replaced
            store.Save("SERIAL-B", Schedule(2, OtherId, 11));
            store.Remove("SERIAL-B", 2);

            var reloaded = ScheduleStore.Load(path);

            Assert.Equal(10, reloaded.For("SERIAL-A").Single().Hour);
            Assert.Empty(reloaded.For("SERIAL-B"));
            Assert.Empty(reloaded.For("SERIAL-C"));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void AnUnreadableStoredListStartsEmpty()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mydyson-schedules-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, "{ not json");
            Assert.Empty(ScheduleStore.Load(path).For("SERIAL-A"));
        }
        finally { File.Delete(path); }
    }

    private static JdmProperties Jdm(string json)
    {
        var jdm = new JdmProperties();
        jdm.Merge((JsonObject)JsonNode.Parse(json)!);
        return jdm;
    }
}
