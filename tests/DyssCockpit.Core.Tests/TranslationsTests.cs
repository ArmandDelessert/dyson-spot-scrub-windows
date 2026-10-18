using System.Globalization;
using System.Text.RegularExpressions;
using DyssCockpit.Core;

namespace DyssCockpit.Core.Tests;

/// <summary>
/// The texts of every language agree with the English ones where a mistake would show on screen: the same placeholders, and
/// nothing that the English does not have. A text a language lacks is not a failure: Windows then shows the English one.
/// </summary>
[Collection("Language")]
public sealed partial class TranslationsTests : IDisposable
{
    public void Dispose() => Translation.Current = AppLanguage.French;

    public static TheoryData<AppLanguage> Languages => [.. AppLanguages.All.Select(i => i.Language)];

    public static TheoryData<AppLanguage> OtherLanguages =>
        [.. AppLanguages.All.Select(i => i.Language).Where(l => l is not (AppLanguage.French or AppLanguage.English))];

    [GeneratedRegex(@"\{(\d+)(?::[^}]*)?\}")]
    private static partial Regex Placeholder();

    private static List<string> PlaceholdersOf(string text) => [.. Placeholder().Matches(text).Select(m => m.Value).Order(StringComparer.Ordinal)];

    [Fact]
    public void TheLanguagesHaveDistinctTagsThatAreCulturesAndANameOfTheirOwn()
    {
        Assert.Equal(AppLanguages.All.Count, AppLanguages.All.Select(i => i.Tag).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(Enum.GetValues<AppLanguage>().Length, AppLanguages.All.Count);
        Assert.Equal(AppLanguages.All.Select(i => i.Tag).Order(StringComparer.Ordinal), ReswStrings.Languages);
        Assert.All(AppLanguages.All, i =>
        {
            Assert.Equal(i.Tag, CultureInfo.GetCultureInfo(i.Tag).Name);
            Assert.False(string.IsNullOrWhiteSpace(i.Name));
            Assert.Equal(i.Language, AppLanguages.FromTag(i.Tag));
        });
    }

    [Theory]
    [MemberData(nameof(OtherLanguages))]
    public void ALanguageKeepsThePlaceholdersOfTheEnglishTexts(AppLanguage language)
    {
        var english = ReswStrings.Resources("en-US");
        var table = ReswStrings.Resources(AppLanguages.Of(language).Tag);

        Assert.NotEmpty(table);
        foreach (var (key, text) in table)
        {
            Assert.True(english.TryGetValue(key, out var source), $"{language}: '{key}' does not exist in English");
            Assert.False(string.IsNullOrWhiteSpace(text), $"{language}: '{key}' is empty");
            Assert.True(PlaceholdersOf(source!).SequenceEqual(PlaceholdersOf(text)), $"{language}: '{key}' does not keep the placeholders of '{source}'");
        }
    }

    [Theory]
    [MemberData(nameof(OtherLanguages))]
    public void ATranslatedTextComesBackInItsLanguage(AppLanguage language)
    {
        Translation.Current = language;

        Assert.NotEqual("Quit", Translation.T("App_Quit"));
        Assert.Contains("boom", Translation.Tf("App_ExportFailed", "boom"), StringComparison.Ordinal);
        Assert.NotEqual("Export failed: boom", Translation.Tf("App_ExportFailed", "boom"));
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public void EveryLanguageNamesTheDaysAndTheRoomTypes(AppLanguage language)
    {
        Translation.Current = language;

        var days = ScheduleDayLabels.All;
        Assert.Equal(7, days.Count);
        Assert.All(days, d => Assert.False(string.IsNullOrWhiteSpace(d.Letter + d.Short + d.Long)));
        Assert.Equal(7, days.Select(d => d.Long).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(30, RoomTypeLabels.All.Count);
        Assert.All(RoomTypeLabels.All, t => Assert.False(string.IsNullOrWhiteSpace(t.Label)));
        foreach (var set in new[] { ScheduleDays.Monday, ScheduleDays.Weekdays, ScheduleDays.Weekend, ScheduleDays.Monday | ScheduleDays.Thursday })
        {
            Assert.False(string.IsNullOrWhiteSpace(ScheduleDayLabels.Describe(set)));
        }
    }
}
