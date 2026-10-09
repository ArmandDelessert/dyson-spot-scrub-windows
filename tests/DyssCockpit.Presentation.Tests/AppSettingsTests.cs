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
    [InlineData("de-DE", "")]          // a language the application does not have: Windows' own
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
        // What tests and previews use: the settings work for the run without touching %APPDATA%.
        var settings = new AppSettings();
        var changes = 0;
        settings.Changed += () => changes++;

        settings.ShowFurniture = false;

        Assert.False(settings.ShowFurniture);
        Assert.Equal(1, changes);
        Assert.False(File.Exists(_path));
    }
}
