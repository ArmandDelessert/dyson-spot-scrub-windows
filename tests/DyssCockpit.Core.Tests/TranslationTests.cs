using System.Globalization;
using DyssCockpit.Core;

namespace DyssCockpit.Core.Tests;

/// <summary>Tests that switch the language run alone, the others expecting French throughout.</summary>
[CollectionDefinition("Language", DisableParallelization = true)]
public sealed class LanguageSwitching;

[Collection("Language")]
public sealed class TranslationTests : IDisposable
{
    public void Dispose() => Translation.Current = AppLanguage.French;

    [Theory]
    [InlineData("fr-CH", AppLanguage.French)]
    [InlineData("fr-FR", AppLanguage.French)]
    [InlineData("en-US", AppLanguage.English)]
    [InlineData("de-CH", AppLanguage.German)]
    [InlineData("it-IT", AppLanguage.Italian)]
    [InlineData("pt-PT", AppLanguage.Portuguese)]
    [InlineData("zh-CN", AppLanguage.ChineseSimplified)]
    [InlineData("zh-Hans-SG", AppLanguage.ChineseSimplified)]
    [InlineData("zh-TW", AppLanguage.English)]    // the traditional Chinese is not offered
    [InlineData("ru-RU", AppLanguage.English)]    // nor Russian: English is the language most likely understood
    public void WindowsInALanguageOfTheApplicationGivesItAndAnyOtherLanguageEnglish(string culture, AppLanguage expected) =>
        Assert.Equal(expected, Translation.FromCulture(new CultureInfo(culture)));

    [Fact]
    public void EachTextComesInTheCurrentLanguage()
    {
        Assert.Equal("Quitter", Translation.T("App_Quit"));
        Translation.Current = AppLanguage.English;
        Assert.Equal("Quit", Translation.T("App_Quit"));
    }

    [Fact]
    public void ATextWithValuesIsFilledInTheCurrentCulture()
    {
        Assert.Equal("Export impossible : boom", Translation.Tf("App_ExportFailed", "boom"));
        Translation.Current = AppLanguage.English;
        Assert.Equal("Export failed: boom", Translation.Tf("App_ExportFailed", "boom"));
    }

    [Fact]
    public void ATextNoResourceHasIsShownAsItsKeySoThatTheGapShows()
    {
        Assert.Equal("No_such_text", Translation.T("No_such_text"));
    }

    [Fact]
    public void TheRobotsLabelsAreTranslatedToo()
    {
        Translation.Current = AppLanguage.English;

        Assert.Equal("Living room", RoomTypeLabels.DefaultNameFor("livingRoom"));
        Assert.Equal("Custom", RoomTypeLabels.TypeHint("custom", "Salon12"));
        Assert.Contains(RoomTypeLabels.All, t => t is ("kitchen", "Kitchen"));
        Assert.Equal("Unreachable", CleanStatusLabels.Resolve("CANT_CLEAN"));
        Assert.Equal("on Monday", ScheduleDayLabels.Describe(ScheduleDays.Monday));
        Assert.Equal("Mon, Thu", ScheduleDayLabels.Describe(ScheduleDays.Monday | ScheduleDays.Thursday));
        Assert.Equal("Monday to Friday", ScheduleDayLabels.Describe(ScheduleDays.Weekdays));
    }

    [Fact]
    public void ARoomsStoredNameIsNeverTranslated()
    {
        // Only the labels the application gives are; the names on the account stay as they are.
        Translation.Current = AppLanguage.English;
        Assert.Equal("Salon12", RoomTypeLabels.Resolve("livingRoom", "Salon12", "14"));
    }
}
