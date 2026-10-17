using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;

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

/// <summary>
/// The language of everything shown to the user. A text is written once in the code with its French and its English
/// wording side by side, <c>T("Tableau de bord", "Dashboard")</c>, so neither can be forgotten or drift from the other.
/// The other languages are in <c>Translations\{tag}.json</c>, a table from the English wording to its translation:
/// a text without one is shown in English. The language is chosen once at start-up (see <see cref="FromCulture"/>);
/// texts already on screen are not redrawn when it changes.
/// </summary>
public static class Translation
{
    private static readonly ConcurrentDictionary<AppLanguage, IReadOnlyDictionary<string, string>> s_tables = new();

    /// <summary>French for a French Windows, a language of the application for its own, English for any other.</summary>
    public static AppLanguage Current { get; set; } = FromCulture(CultureInfo.CurrentUICulture);

    public static bool IsFrench => Current == AppLanguage.French;

    /// <summary>The culture of the language, for the numbers and the dates written in it.</summary>
    public static CultureInfo Culture => CultureInfo.GetCultureInfo(AppLanguages.Of(Current).Tag);

    /// <summary>The wording of a text in the current language.</summary>
    public static string T(string french, string english) => Current switch
    {
        AppLanguage.French => french,
        AppLanguage.English => english,
        _ => Find(english) ?? english,
    };

    /// <summary>
    /// The wording of a text that has values in it, <c>Tf($"{n} pièces", $"{n} rooms")</c>. A translation is the English
    /// text with its holes as <c>{0}</c>, <c>{1:N0}</c>… in the order they come in the English; it may move them.
    /// </summary>
    public static string Tf(FormattableString french, FormattableString english) => Current switch
    {
        AppLanguage.French => french.ToString(CultureInfo.CurrentCulture),
        AppLanguage.English => english.ToString(CultureInfo.CurrentCulture),
        _ => Find(english.Format) is { } format
            ? string.Format(CultureInfo.CurrentCulture, format, english.GetArguments())
            : english.ToString(CultureInfo.CurrentCulture),
    };

    /// <summary>The language of the machine when the application speaks it, English otherwise: the language most likely understood.</summary>
    public static AppLanguage FromCulture(CultureInfo culture) => AppLanguages.FromTag(culture.Name) ?? AppLanguage.English;

    /// <summary>How the list of a few short items is joined: a comma and a space, the ideographic comma in Chinese and Japanese.</summary>
    public static string ListSeparator => Current is AppLanguage.Japanese or AppLanguage.ChineseSimplified ? "、" : ", ";

    private static string? Find(string english) =>
        s_tables.GetOrAdd(Current, Load).TryGetValue(english, out var text) ? text : null;

    private static Dictionary<string, string> Load(AppLanguage language)
    {
        var name = $"DyssCockpit.Core.Translations.{AppLanguages.Of(language).Tag}.json";
        using var stream = typeof(Translation).Assembly.GetManifestResourceStream(name);
        return stream is null ? [] : JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? [];
    }

    /// <summary>The texts a language has translated, English wording to its translation; for the tests.</summary>
    internal static IReadOnlyDictionary<string, string> TableOf(AppLanguage language) => s_tables.GetOrAdd(language, Load);
}
