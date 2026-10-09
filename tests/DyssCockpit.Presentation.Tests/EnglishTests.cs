using System.Text.Json.Nodes;
using DyssCockpit.Core;
using DyssCockpit.Presentation.Services;
using DyssCockpit.Presentation.ViewModels;

namespace DyssCockpit.Presentation.Tests;

/// <summary>Tests that switch the language run alone, the others expecting French throughout.</summary>
[CollectionDefinition("Language", DisableParallelization = true)]
public sealed class LanguageSwitching;

[Collection("Language")]
public sealed class EnglishTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"dyss-settings-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        Translation.Current = AppLanguage.French;
        if (File.Exists(_path)) File.Delete(_path);
    }

    [Fact]
    public void TheStateCardSpeaksEnglish()
    {
        Translation.Current = AppLanguage.English;
        var vm = new StatusViewModel(TestHub.Create());

        vm.Apply(RobotState.Parse("""{"msg":"CURRENT-STATE","state":"FULL_CLEAN_PAUSED","dockState":"DRYING_MOP","fullCleanAction":"VACUUMING"}""")!);

        Assert.Equal("Paused", vm.StateText);
        Assert.Equal("vacuuming", vm.ActionText);
        Assert.Equal("Dock: drying the mop roller", vm.DockText);
        Assert.Equal("Stop drying", vm.WashDryLabel);
        Assert.Equal("Resume", vm.PauseResumeLabel);
    }

    [Fact]
    public void TheCleaningNotificationsSpeakEnglish()
    {
        Translation.Current = AppLanguage.English;
        var api = new DysonCloudClient("CH", "fr-CH", http: new HttpClient(new TestHub.RouteHandler())) { BearerToken = "test-token" };
        var stored = new StoredSession("test@example.invalid", "CH", "fr-CH", null, "test-token", DateTimeOffset.UnixEpoch);
        using var vm = new MainViewModel(RobotContext.FromLogin(api, stored), new TestHub.InlineDispatcher(), new FakeDialogs(),
            settings: new AppSettings { TaskNotifications = TaskNotificationMode.StartAndEnd });
        var sent = new List<(string Title, string Body)>();
        vm.NotifyRequested += (title, body) => sent.Add((title, body));
        static RobotState State(string state) => RobotState.Parse($$$"""{"msg":"CURRENT-STATE","state":"{{{state}}}"}""")!;
        static JsonObject Report(int status, long startedAt) =>
            JsonNode.Parse($$$"""{"params":{"record_start_time":{{{startedAt}}},"record_task_status":{{{status}}},"record_clean_mode":0,"record_use_time":70}}""")!.AsObject();

        vm.OnRobotState(State("INACTIVE_CHARGING"));
        vm.OnRobotState(State("FULL_CLEAN_RUNNING"));
        vm.OnRobotEvent("event.clean_finish.post", new JsonObject());
        vm.OnRobotEvent("event.clean_record.post", Report(status: 1, startedAt: 1));
        vm.OnRobotState(State("INACTIVE_CHARGING"));
        vm.OnRobotState(State("FULL_CLEAN_RUNNING"));
        vm.OnRobotEvent("event.clean_record.post", Report(status: 4, startedAt: 2));

        Assert.Equal(
            [("Cleaning started", "The robot has started cleaning."),
             ("Cleaning finished", "The robot has finished cleaning in 1 h 10."),
             ("Cleaning started", "The robot has started cleaning."),
             ("Cleaning interrupted", "The robot gave up cleaning.")],
            sent);
    }

    [Fact]
    public void TheLanguageIsWindowsOwnUntilOneIsChosenThenRemembered()
    {
        var first = AppSettings.Load(_path);
        Assert.Equal("", first.Language);

        first.Language = "en-US";
        var second = AppSettings.Load(_path);
        Assert.Equal("en-US", second.Language);
        Assert.Equal(AppLanguage.English, second.ChosenLanguage);

        second.Language = "fr-FR";
        Assert.Equal(AppLanguage.French, AppSettings.Load(_path).ChosenLanguage);
    }
}
