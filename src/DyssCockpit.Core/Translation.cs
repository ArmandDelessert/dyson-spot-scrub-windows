using System.Globalization;

namespace DyssCockpit.Core;

/// <summary>The languages the application speaks.</summary>
public enum AppLanguage
{
    French,
    English,
    German,
    Spanish,
    Italian,
    Dutch,
    Polish,
    Portuguese,
    Romanian,
    Turkish,
    Ukrainian,
    Japanese,
    Korean,
    ChineseSimplified,
}

/// <summary>What the application knows of its languages: the tag of each, and its name in that language.</summary>
public static class AppLanguages
{
    /// <summary>One language of the application.</summary>
    /// <param name="Language">The language.</param>
    /// <param name="Tag">Its tag, which is also the name of its file in Translations (<c>de-DE.json</c>) and what the settings store.</param>
    /// <param name="Name">Its name in that language: the language list shows each one under its own, so it can be found whatever the current one.</param>
    public sealed record Info(AppLanguage Language, string Tag, string Name);

    /// <summary>
    /// The languages in the order the settings list them: French and English, whose texts are in the code,
    /// then the others by name; each of these has its texts in <c>Translations\{tag}.json</c>.
    /// </summary>
    public static IReadOnlyList<Info> All { get; } =
    [
        new(AppLanguage.French, "fr-FR", "Français"),
        new(AppLanguage.English, "en-US", "English"),
        new(AppLanguage.German, "de-DE", "Deutsch"),
        new(AppLanguage.Spanish, "es-ES", "Español"),
        new(AppLanguage.Italian, "it-IT", "Italiano"),
        new(AppLanguage.Dutch, "nl-NL", "Nederlands"),
        new(AppLanguage.Polish, "pl-PL", "Polski"),
        new(AppLanguage.Portuguese, "pt-BR", "Português (Brasil)"),
        new(AppLanguage.Romanian, "ro-RO", "Română"),
        new(AppLanguage.Turkish, "tr-TR", "Türkçe"),
        new(AppLanguage.Ukrainian, "uk-UA", "Українська"),
        new(AppLanguage.Japanese, "ja-JP", "日本語"),
        new(AppLanguage.Korean, "ko-KR", "한국어"),
        new(AppLanguage.ChineseSimplified, "zh-CN", "简体中文"),
    ];

    public static Info Of(AppLanguage language) => All.Single(i => i.Language == language);

    /// <summary>
    /// The language of a culture name such as <c>"fr-CH"</c> or <c>"pt-PT"</c>: the same language whatever the country.
    /// Chinese is the simplified one only; null for a language the application does not speak.
    /// </summary>
    public static AppLanguage? FromTag(string? tag)
    {
        var name = tag?.Trim() ?? "";
        var language = name.Split('-', '_')[0].ToLowerInvariant();
        if (language == "zh")
        {
            var traditional = name.Contains("TW", StringComparison.OrdinalIgnoreCase) || name.Contains("HK", StringComparison.OrdinalIgnoreCase)
                || name.Contains("MO", StringComparison.OrdinalIgnoreCase) || name.Contains("Hant", StringComparison.OrdinalIgnoreCase);
            return traditional ? null : AppLanguage.ChineseSimplified;
        }
        return All.Select(i => (AppLanguage?)i.Language)
            .FirstOrDefault(l => string.Equals(Of(l!.Value).Tag.Split('-')[0], language, StringComparison.Ordinal));
    }
}

/// <summary>Where the texts of the application come from: the resources of the application, or in the tests the .resw files read as they are.</summary>
public interface IStringSource
{
    /// <summary>The text of <paramref name="key"/> in the language the source is set to, or null when there is none.</summary>
    string? Find(string key);
}

/// <summary>
/// The language of everything shown to the user, and the texts of the code. A text of the code has a key,
/// <c>T("Dashboard_Title")</c>, and its words in every language are in the resources of the application,
/// <c>Strings\{tag}\Resources.resw</c>, which tools/strings generates from ui_strings.py (English and French)
/// and translations\{tag}.json (the others). The texts of the XAML are the resources named by their <c>x:Uid</c>.
/// The language is chosen once at start-up (see <see cref="FromCulture"/>); texts already on screen are not redrawn when it changes.
/// </summary>
public static class Translation
{
    /// <summary>French for a French Windows, a language of the application for its own, English for any other.</summary>
    public static AppLanguage Current { get; set; } = FromCulture(CultureInfo.CurrentUICulture);

    /// <summary>Where the texts are read; the application and the tests each set theirs before anything is shown.</summary>
    public static IStringSource? Source { get; set; }

    public static bool IsFrench => Current == AppLanguage.French;

    /// <summary>The culture of the language, for the numbers and the dates written in it.</summary>
    public static CultureInfo Culture => CultureInfo.GetCultureInfo(AppLanguages.Of(Current).Tag);

    /// <summary>The text of <paramref name="key"/> in the current language; the key itself when there is no text, so that a gap shows.</summary>
    public static string T(string key) => Source?.Find(key) is { Length: > 0 } text ? text : key;

    /// <summary>The text of <paramref name="key"/> with its values: <c>{0}</c>, <c>{1:N0}</c>… are filled in the current culture.</summary>
    public static string Tf(string key, params object?[] values) => string.Format(CultureInfo.CurrentCulture, T(key), values);

    /// <summary>The language of the machine when the application speaks it, English otherwise: the language most likely understood.</summary>
    public static AppLanguage FromCulture(CultureInfo culture) => AppLanguages.FromTag(culture.Name) ?? AppLanguage.English;

    /// <summary>How the items of a short list are joined: the comma of the language and a space, none after the ideographic comma of Chinese and Japanese.</summary>
    public static string ListSeparator
    {
        get
        {
            var comma = T("List_Separator");
            return comma is "、" or "，" ? comma : comma + " ";
        }
    }
}
