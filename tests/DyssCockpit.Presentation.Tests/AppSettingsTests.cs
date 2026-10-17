using DyssCockpit.Presentation.Services;

namespace DyssCockpit.Presentation.Tests;

public sealed class AppSettingsTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"dyss-settings-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    [Fact]
    public void TheMapDrawsEverythingButTheExportButtonAndTheSmoothingByDefault()
    {
        var settings = AppSettings.Load(_path);

        Assert.True(settings.ShowFurniture);
        Assert.True(settings.ShowTravelPath);
        Assert.True(settings.ShowCleanedArea);
        Assert.False(settings.ShowExportButton);
        Assert.False(settings.SmoothRobotMotion);
    }

    [Fact]
    public void TheWindowsPlaceIsRememberedAcrossRunsWithoutTellingTheMapsToRedraw()
    {
        var first = AppSettings.Load(_path);
        Assert.Null(first.Window);
        var changes = 0;
        first.Changed += () => changes++;

        first.RememberWindow(new WindowBounds(510, 309, 1860, 1230, Maximized: true));

        Assert.Equal(0, changes);   // a window being dragged is no reason to redraw the maps
        Assert.Equal(new WindowBounds(510, 309, 1860, 1230, true), AppSettings.Load(_path).Window);
        Assert.Contains("\"Window\": {", File.ReadAllText(_path), StringComparison.Ordinal);
    }

    [Fact]
    public void AWindowPlaceIsKeptWhenAnotherSettingIsSavedAndAnEmptyOneIsIgnored()
    {
        var settings = AppSettings.Load(_path);
        settings.RememberWindow(new WindowBounds(10, 20, 1200, 800, false));

        settings.SnapToGrid = false;   // writes the whole file again

        Assert.Equal(new WindowBounds(10, 20, 1200, 800, false), AppSettings.Load(_path).Window);

        File.WriteAllText(_path, """{"Window":{"X":0,"Y":0,"Width":0,"Height":0,"Maximized":false}}""");
        Assert.Null(AppSettings.Load(_path).Window);
    }

    [Fact]
    public void TheScreenshotNeverLeavesAWindowPlaceBehind()
    {
        AppSettings.Load(_path).RememberWindow(new WindowBounds(1, 2, 1000, 700, false));
        var before = File.ReadAllText(_path);

        AppSettings.LoadReadOnly(_path).RememberWindow(new WindowBounds(9, 9, 2000, 1500, true));

        Assert.Equal(before, File.ReadAllText(_path));
    }

    [Fact]
    public void TheFileIsWrittenOneSettingALineForAPersonToRead()
    {
        var settings = AppSettings.Load(_path);
        settings.ShowFurniture = false;

        var text = File.ReadAllText(_path);

        Assert.Contains("\n", text, StringComparison.Ordinal);
        Assert.Contains("\"ShowFurniture\": false", text, StringComparison.Ordinal);
        Assert.False(AppSettings.Load(_path).ShowFurniture);
    }

    [Theory]
    [InlineData("fr-FR", "fr-FR")]
    [InlineData("en-US", "en-US")]
    [InlineData("", "")]
    [InlineData("fr", "fr-FR")]        // the first versions
    [InlineData("en", "en-US")]
    [InlineData("auto", "")]
    [InlineData("fr-CH", "fr-FR")]     // any French is French
    [InlineData("EN-gb", "en-US")]
    [InlineData("de-DE", "de-DE")]     // the languages of the translations
    [InlineData("de", "de-DE")]
    [InlineData("pt-PT", "pt-BR")]     // one Portuguese for all
    [InlineData("zh-Hans-CN", "zh-CN")]
    [InlineData("zh-TW", "")]          // not the traditional Chinese
    [InlineData("ru-RU", "")]          // a language the application does not have: Windows' own
    [InlineData(null, "")]
    public void TheLanguageIsStoredAsACultureNameAndTheOldValuesAreStillRead(string? stored, string expected)
    {
        File.WriteAllText(_path, stored is null ? "{}" : $$"""{"Language":"{{stored}}"}""");

        Assert.Equal(expected, AppSettings.Load(_path).Language);
        Assert.Equal(expected, AppSettings.NormalizeLanguage(stored));
    }

    [Fact]
    public void AnOldLanguageValueIsWrittenBackAsACultureNameAtTheNextSave()
    {
        File.WriteAllText(_path, """{"Language":"fr","ShowFurniture":true}""");
        var settings = AppSettings.Load(_path);

        settings.SnapToGrid = false;   // any change saves

        Assert.Contains("\"Language\": \"fr-FR\"", File.ReadAllText(_path), StringComparison.Ordinal);
    }

    [Fact]
    public void LogsAreKeptAWeekAndMessagesAMonthByDefault()
    {
        var settings = AppSettings.Load(_path);

        Assert.Equal(7, settings.LogRetentionDays);
        Assert.Equal(30, settings.MessageRetentionDays);
    }

    [Fact]
    public void CleansAreNotKeptByDefaultAndForAYearWhenTheyAre()
    {
        var first = AppSettings.Load(_path);
        Assert.False(first.ArchiveCleans);
        Assert.Equal(365, first.CleanArchiveRetentionDays);

        first.ArchiveCleans = true;
        first.CleanArchiveRetentionDays = 0;
        var second = AppSettings.Load(_path);

        Assert.True(second.ArchiveCleans);
        Assert.Equal(0, second.CleanArchiveRetentionDays);

        File.WriteAllText(_path, """{"CleanArchiveRetentionDays":45}""");
        Assert.Equal(365, AppSettings.Load(_path).CleanArchiveRetentionDays);   // not one the list offers
    }

    [Fact]
    public void ARetentionIsRememberedAndForEverIsZero()
    {
        var first = AppSettings.Load(_path);
        first.LogRetentionDays = 0;
        first.MessageRetentionDays = 90;

        var second = AppSettings.Load(_path);

        Assert.Equal(0, second.LogRetentionDays);
        Assert.Equal(90, second.MessageRetentionDays);
    }

    [Fact]
    public void ARetentionWrittenByHandThatTheListDoesNotOfferFallsBackToTheDefault()
    {
        File.WriteAllText(_path, """{"LogRetentionDays":10,"MessageRetentionDays":-5}""");

        var settings = AppSettings.Load(_path);

        Assert.Equal(7, settings.LogRetentionDays);
        Assert.Equal(30, settings.MessageRetentionDays);
    }

    [Fact]
    public void AChangeIsRememberedAcrossRuns()
    {
        var first = AppSettings.Load(_path);
        first.ShowFurniture = false;
        first.ShowExportButton = false;

        var second = AppSettings.Load(_path);

        Assert.False(second.ShowFurniture);
        Assert.True(second.ShowTravelPath);
        Assert.False(second.ShowExportButton);
    }


    [Fact]
    public void AScreenshotReadsThePreferencesButNeverWritesThem()
    {
        // A screenshot may run beside the application: it must not touch the user's file, nor
        // record messages into the daily file the application is writing.
        var user = AppSettings.Load(_path);
        user.ShowFurniture = false;
        user.RecordMessages = true;
        var before = File.ReadAllText(_path);

        var shot = AppSettings.LoadReadOnly(_path);
        Assert.False(shot.ShowFurniture);
        Assert.False(shot.RecordMessages);
        Assert.True(shot.LeavesFilesAlone);
        Assert.False(AppSettings.Load(_path).LeavesFilesAlone);
        shot.ShowFurniture = true;
        shot.Language = "en-US";

        Assert.Equal(before, File.ReadAllText(_path));
        Assert.True(AppSettings.Load(_path).RecordMessages);
    }

    [Fact]
    public void TheCleanedAreaIsOnUntilTurnedOffThenRemembered()
    {
        var first = AppSettings.Load(_path);
        Assert.True(first.ShowCleanedArea);
        first.ShowCleanedArea = false;

        Assert.False(AppSettings.Load(_path).ShowCleanedArea);
    }

    [Fact]
    public void SmoothingTheRobotIsOffUntilChosenThenRemembered()
    {
        var first = AppSettings.Load(_path);
        Assert.False(first.SmoothRobotMotion);
        first.SmoothRobotMotion = true;

        Assert.True(AppSettings.Load(_path).SmoothRobotMotion);
    }
    [Fact]
    public void LoadingDoesNotLookLikeAChange()
    {
        // Otherwise every launch would redraw both maps before anything was even displayed.
        var first = AppSettings.Load(_path);
        first.ShowTravelPath = false;

        var changes = 0;
        var second = AppSettings.Load(_path);
        second.Changed += () => changes++;

        Assert.False(second.ShowTravelPath);
        Assert.Equal(0, changes);

        second.ShowTravelPath = true;
        Assert.Equal(1, changes);
    }

    [Fact]
    public void AFileWrittenByAnOlderVersionKeepsTheDefaultsForWhatItLacks()
    {
        File.WriteAllText(_path, """{"ShowFurniture":false}""");

        var settings = AppSettings.Load(_path);

        Assert.False(settings.ShowFurniture);
        Assert.True(settings.ShowTravelPath);
        Assert.True(settings.ShowCleanedArea);
        Assert.False(settings.ShowExportButton);
    }

    [Fact]
    public void AnUnreadableFileFallsBackToTheDefaultsInsteadOfFailingTheLaunch()
    {
        File.WriteAllText(_path, "{ this is not json");

        var settings = AppSettings.Load(_path);

        Assert.True(settings.ShowFurniture);
        Assert.True(settings.ShowTravelPath);
    }

    [Fact]
    public void ClosingTheWindowKeepsTheAppInTheNotificationAreaUnlessTurnedOff()
    {
        var first = AppSettings.Load(_path);
        Assert.True(first.CloseToTray);
        Assert.False(first.TrayHintShown);

        first.CloseToTray = false;
        first.TrayHintShown = true;
        var second = AppSettings.Load(_path);

        Assert.False(second.CloseToTray);
        Assert.True(second.TrayHintShown);
    }

    [Fact]
    public void AFileFromBeforeTheNotificationAreaKeepsTheAppThere()
    {
        File.WriteAllText(_path, """{"ShowFurniture":false,"RecordMessages":true}""");

        var settings = AppSettings.Load(_path);

        Assert.True(settings.CloseToTray);
        Assert.False(settings.TrayHintShown);
        Assert.True(settings.RecordMessages);
    }

    [Fact]
    public void AnInstanceWithNoFileBehavesButWritesNothing()
    {
        // What tests and previews use: the settings work for the run without touching %AppData%.
        var settings = new AppSettings();
        var changes = 0;
        settings.Changed += () => changes++;

        settings.ShowFurniture = false;

        Assert.False(settings.ShowFurniture);
        Assert.Equal(1, changes);
        Assert.False(File.Exists(_path));
    }

    // ---- The file format, unchanged by the class becoming its own format ------------------------

    /// <summary>A file as the version before wrote it, every setting away from its default.</summary>
    private const string EveryKeyAwayFromItsDefault = """
        {
          "ShowFurniture": false,
          "ShowTravelPath": false,
          "ShowExportButton": true,
          "RecordMessages": true,
          "SnapToGrid": false,
          "SmoothRobotMotion": true,
          "Language": "en-US",
          "CloseToTray": false,
          "TrayHintShown": true,
          "ShowCleanedArea": false,
          "TaskNotifications": "StartAndEnd",
          "NotifyUnreachable": false,
          "LogRetentionDays": 14,
          "MessageRetentionDays": 90,
          "NotifyRobotFault": false,
          "Window": { "X": 10, "Y": 20, "Width": 1200, "Height": 800, "Maximized": true },
          "ArchiveCleans": true,
          "CleanArchiveRetentionDays": 730,
          "ShowPointerCoordinates": true,
          "ShowShapeDimensions": true
        }
        """;

    [Fact]
    public void AFileOfThePreviousVersionIsReadSettingBySetting()
    {
        File.WriteAllText(_path, EveryKeyAwayFromItsDefault);

        var s = AppSettings.Load(_path);

        Assert.Equal((false, false, true, true, false, true), (s.ShowFurniture, s.ShowTravelPath, s.ShowExportButton, s.RecordMessages, s.SnapToGrid, s.SmoothRobotMotion));
        Assert.Equal(("en-US", false, true, false), (s.Language, s.CloseToTray, s.TrayHintShown, s.ShowCleanedArea));
        Assert.Equal((TaskNotificationMode.StartAndEnd, false, false), (s.TaskNotifications, s.NotifyUnreachable, s.NotifyRobotFault));
        Assert.Equal((14, 90, true, 730), (s.LogRetentionDays, s.MessageRetentionDays, s.ArchiveCleans, s.CleanArchiveRetentionDays));
        Assert.Equal(new WindowBounds(10, 20, 1200, 800, true), s.Window);
        Assert.Equal((true, true), (s.ShowPointerCoordinates, s.ShowShapeDimensions));
    }

    [Fact]
    public void WrittenBackItHasTheSameKeysAndValuesAndNothingElse()
    {
        File.WriteAllText(_path, EveryKeyAwayFromItsDefault);
        var settings = AppSettings.Load(_path);

        settings.ShowFurniture = true;
        settings.ShowFurniture = false;   // written twice, ending where it was

        using var written = System.Text.Json.JsonDocument.Parse(File.ReadAllText(_path));
        using var before = System.Text.Json.JsonDocument.Parse(EveryKeyAwayFromItsDefault);
        Assert.Equal(before.RootElement.EnumerateObject().Select(p => p.Name).Order(), written.RootElement.EnumerateObject().Select(p => p.Name).Order());
        Assert.True(System.Text.Json.JsonElement.DeepEquals(before.RootElement, written.RootElement), File.ReadAllText(_path));
    }

    [Theory]
    [InlineData("\"Sometimes\"")]
    [InlineData("2")]
    [InlineData("{}")]
    public void ANotificationChoiceThisVersionDoesNotKnowIsTheDefaultAndTheRestIsKept(string value)
    {
        File.WriteAllText(_path, $$"""{"TaskNotifications": {{value}}, "ShowFurniture": false}""");

        var settings = AppSettings.Load(_path);

        Assert.Equal(TaskNotificationMode.EndOnly, settings.TaskNotifications);
        Assert.False(settings.ShowFurniture);
    }

    [Fact]
    public void AnEmptyWindowIsForgottenAndUnknownKeysAreIgnored()
    {
        File.WriteAllText(_path, """{"Window": {"X": 0, "Y": 0, "Width": 0, "Height": 0, "Maximized": false}, "SomethingNew": 3, "ShowFurniture": false}""");

        var settings = AppSettings.Load(_path);

        Assert.Null(settings.Window);
        Assert.False(settings.ShowFurniture);
    }
}
