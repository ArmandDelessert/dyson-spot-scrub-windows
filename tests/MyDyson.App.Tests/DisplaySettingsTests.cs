using System.IO;
using MyDyson.App.Services;

namespace MyDyson.App.Tests;

public sealed class DisplaySettingsTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"mydyson-display-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    [Fact]
    public void EverythingIsOnByDefault()
    {
        // The map has always drawn all of it; a preferences file that does not exist yet must not
        // change what an existing user sees.
        var settings = DisplaySettings.Load(_path);

        Assert.True(settings.ShowFurniture);
        Assert.True(settings.ShowTravelPath);
        Assert.True(settings.ShowExportButton);
    }

    [Fact]
    public void AChangeIsRememberedAcrossRuns()
    {
        var first = DisplaySettings.Load(_path);
        first.ShowFurniture = false;
        first.ShowExportButton = false;

        var second = DisplaySettings.Load(_path);

        Assert.False(second.ShowFurniture);
        Assert.True(second.ShowTravelPath);
        Assert.False(second.ShowExportButton);
    }

    [Fact]
    public void LoadingDoesNotLookLikeAChange()
    {
        // Otherwise every launch would redraw both maps before anything was even displayed.
        var first = DisplaySettings.Load(_path);
        first.ShowTravelPath = false;

        var changes = 0;
        var second = DisplaySettings.Load(_path);
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

        var settings = DisplaySettings.Load(_path);

        Assert.False(settings.ShowFurniture);
        Assert.True(settings.ShowTravelPath);
        Assert.True(settings.ShowExportButton);
    }

    [Fact]
    public void AnUnreadableFileFallsBackToTheDefaultsInsteadOfFailingTheLaunch()
    {
        File.WriteAllText(_path, "{ this is not json");

        var settings = DisplaySettings.Load(_path);

        Assert.True(settings.ShowFurniture);
        Assert.True(settings.ShowTravelPath);
    }

    [Fact]
    public void AnInstanceWithNoFileBehavesButWritesNothing()
    {
        // What tests and previews use: the settings work for the run without touching %APPDATA%.
        var settings = new DisplaySettings();
        var changes = 0;
        settings.Changed += () => changes++;

        settings.ShowFurniture = false;

        Assert.False(settings.ShowFurniture);
        Assert.Equal(1, changes);
        Assert.False(File.Exists(_path));
    }
}
