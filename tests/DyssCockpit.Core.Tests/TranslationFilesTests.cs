using System.Globalization;
using System.Text.RegularExpressions;
using DyssCockpit.Core;

namespace DyssCockpit.Core.Tests;

/// <summary>
/// The tables of the languages other than French and English (Translations\{tag}.json) are usable: every text keeps the
/// placeholders and the spaces at the ends of its English, and the language is spoken everywhere the code speaks it.
/// What a table lacks is not a failure: the English text is shown meanwhile.
/// </summary>
[Collection("Language")]
public sealed partial class TranslationFilesTests : IDisposable
{
    public void Dispose() => Translation.Current = AppLanguage.French;

    public static TheoryData<AppLanguage> Languages =>
        [.. AppLanguages.All.Select(i => i.Language).Where(l => l is not (AppLanguage.French or AppLanguage.English))];

    [GeneratedRegex(@"\{(\d+)(?::[^}]*)?\}")]
    private static partial Regex Placeholder();

    private static List<string> PlaceholdersOf(string text) => [.. Placeholder().Matches(text).Select(m => m.Value).Order(StringComparer.Ordinal)];

    private static (string, string) Edges(string text) => (text[..(text.Length - text.TrimStart().Length)], text[text.TrimEnd().Length..]);

    [Fact]
    public void TheLanguagesHaveDistinctTagsThatAreCulturesAndANameOfTheirOwn()
    {
        Assert.Equal(AppLanguages.All.Count, AppLanguages.All.Select(i => i.Tag).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(Enum.GetValues<AppLanguage>().Length, AppLanguages.All.Count);
        Assert.All(AppLanguages.All, i =>
        {
            Assert.Equal(i.Tag, CultureInfo.GetCultureInfo(i.Tag).Name);
            Assert.False(string.IsNullOrWhiteSpace(i.Name));
            Assert.Equal(i.Language, AppLanguages.FromTag(i.Tag));
        });
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public void ATableKeepsThePlaceholdersAndTheSpacesOfTheEnglishTexts(AppLanguage language)
    {
        var table = Translation.TableOf(language);

        Assert.NotEmpty(table);
        foreach (var (english, text) in table)
        {
            Assert.False(string.IsNullOrWhiteSpace(text), $"{language}: the translation of '{english}' is empty");
            Assert.True(PlaceholdersOf(english).SequenceEqual(PlaceholdersOf(text)), $"{language}: '{text}' does not keep the placeholders of '{english}'");
            Assert.True(Edges(english) == Edges(text), $"{language}: '{text}' does not keep the spaces and line breaks at the ends of '{english}'");
        }
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public void ATranslatedTextComesBackInItsLanguage(AppLanguage language)
    {
        Translation.Current = language;

        var plain = Translation.T("Tableau de bord", "Dashboard");
        var detail = "boom";
        var withValue = Translation.Tf($"La connexion a échoué : {detail}", $"Login failed: {detail}");

        Assert.NotEqual("Tableau de bord", plain);
        Assert.NotEqual("Login failed: boom", withValue);
        Assert.Contains("boom", withValue, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public void ATextNoTableKnowsIsShownInEnglish(AppLanguage language)
    {
        Translation.Current = language;
        var count = 7;

        Assert.Equal("A text that no table has", Translation.T("Un texte qu'aucune table n'a", "A text that no table has"));
        Assert.Equal("7 things that no table has", Translation.Tf($"{count} choses qu'aucune table n'a", $"{count} things that no table has"));
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
        foreach (var day in new[] { ScheduleDays.Monday, ScheduleDays.Weekdays, ScheduleDays.Weekend, ScheduleDays.Monday | ScheduleDays.Thursday })
        {
            Assert.False(string.IsNullOrWhiteSpace(ScheduleDayLabels.Describe(day)));
        }
    }
}
