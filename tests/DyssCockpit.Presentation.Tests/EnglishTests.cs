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
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"dyss-display-{Guid.NewGuid():N}.json");

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
    public void TheLanguageIsWindowsOwnUntilOneIsChosenThenRemembered()
    {
        var first = DisplaySettings.Load(_path);
        Assert.Equal("auto", first.Language);

        first.Language = "en";
        var second = DisplaySettings.Load(_path);
        Assert.Equal("en", second.Language);
        Assert.Equal(AppLanguage.English, second.ChosenLanguage);

        second.Language = "fr";
        Assert.Equal(AppLanguage.French, DisplaySettings.Load(_path).ChosenLanguage);
    }
}
