namespace DyssCockpit.Core;

/// <summary>
/// The "lang" of split_room and arrange_room: the language the robot names the rooms it creates in
/// ("Pièce3", "Room3", "Raum3"…), and it guesses a room type along the way ("Chambre6",
/// "Bedroom6"). Established on 2026-09-29 by splitting the same room of a test map with every value
/// from 0 to 20 and reading the names back. Some languages are only partly translated: Polish,
/// Russian and the Asian ones keep "Bedroom" in English, Portuguese keeps "Room". From 15 upwards,
/// and at 12, everything comes out in English.
/// </summary>
public static class MapLanguage
{
    public const int SimplifiedChinese = 1;
    public const int English = 2;
    public const int Spanish = 3;
    public const int German = 4;
    public const int French = 5;
    public const int Polish = 6;
    public const int Italian = 7;
    public const int Russian = 8;
    public const int TraditionalChinese = 9;
    public const int Thai = 10;
    public const int Korean = 11;
    public const int Portuguese = 13;
    public const int TraditionalChineseHongKong = 14;

    /// <summary>The code for a culture such as "fr-CH" or "zh-TW"; English for a language the robot does not name rooms in.</summary>
    public static int FromCulture(string? culture)
    {
        var c = (culture ?? "").Trim().ToLowerInvariant();
        var language = c.Split('-', '_')[0];
        return language switch
        {
            "fr" => French,
            "en" => English,
            "es" => Spanish,
            "de" => German,
            "pl" => Polish,
            "it" => Italian,
            "ru" => Russian,
            "th" => Thai,
            "ko" => Korean,
            "pt" => Portuguese,
            "zh" when c.Contains("hk", StringComparison.Ordinal) => TraditionalChineseHongKong,
            "zh" when c.Contains("tw", StringComparison.Ordinal) || c.Contains("hant", StringComparison.Ordinal) => TraditionalChinese,
            "zh" => SimplifiedChinese,
            _ => English,
        };
    }
}
