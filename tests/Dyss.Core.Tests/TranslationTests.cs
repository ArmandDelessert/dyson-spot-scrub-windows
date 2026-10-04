using System.Globalization;
using Dyss.Core;

namespace Dyss.Core.Tests;

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
    [InlineData("de-CH", AppLanguage.English)]
    [InlineData("it-IT", AppLanguage.English)]
    public void WindowsInFrenchGivesFrenchAndAnyOtherLanguageEnglish(string culture, AppLanguage expected) =>
        Assert.Equal(expected, Translation.FromCulture(new CultureInfo(culture)));

    [Fact]
    public void EachTextComesInTheCurrentLanguage()
    {
        Assert.Equal("Tableau de bord", Translation.T("Tableau de bord", "Dashboard"));
        Translation.Current = AppLanguage.English;
        Assert.Equal("Dashboard", Translation.T("Tableau de bord", "Dashboard"));
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
