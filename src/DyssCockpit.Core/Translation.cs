using System.Globalization;

namespace DyssCockpit.Core;

/// <summary>The languages the application speaks.</summary>
public enum AppLanguage
{
    French,
    English,
}

/// <summary>
/// The language of everything shown to the user. Each text is written once with both its French
/// and its English wording side by side, <c>T("Tableau de bord", "Dashboard")</c>, so neither can
/// be forgotten or drift from the other. The language is chosen once at start-up (see
/// <see cref="FromCulture"/>); texts already on screen are not redrawn when it changes.
/// </summary>
public static class Translation
{
    /// <summary>French for a French Windows, English for any other.</summary>
    public static AppLanguage Current { get; set; } = FromCulture(CultureInfo.CurrentUICulture);

    public static bool IsEnglish => Current == AppLanguage.English;

    /// <summary>The wording of a text in the current language.</summary>
    public static string T(string french, string english) => IsEnglish ? english : french;

    /// <summary>French when the culture is French, English otherwise: the language most likely understood.</summary>
    public static AppLanguage FromCulture(CultureInfo culture) =>
        culture.TwoLetterISOLanguageName == "fr" ? AppLanguage.French : AppLanguage.English;
}
