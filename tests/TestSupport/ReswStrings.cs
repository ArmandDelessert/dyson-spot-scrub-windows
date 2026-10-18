using System.Collections.Concurrent;
using System.Xml.Linq;
using DyssCockpit.Core;

namespace DyssCockpit.TestSupport;

/// <summary>
/// The application's real texts, read from the .resw files copied next to the tests (Strings\{tag}\Resources.resw), in the
/// language <see cref="Translation.Current"/> names. A text a language lacks is the English one, as Windows shows it.
/// </summary>
internal sealed class ReswStrings : IStringSource
{
    private static readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, string>> s_tables = new(StringComparer.Ordinal);

    public static ReswStrings Instance { get; } = new();

    /// <summary>The folders of Strings: one per language of the application.</summary>
    public static IReadOnlyList<string> Languages { get; } =
        [.. Directory.GetDirectories(Path.Combine(AppContext.BaseDirectory, "Strings")).Select(d => Path.GetFileName(d)!).Order(StringComparer.Ordinal)];

    public string? Find(string key)
    {
        var tag = AppLanguages.Of(Translation.Current).Tag;
        return Resources(tag).TryGetValue(key, out var text) ? text : Resources("en-US").GetValueOrDefault(key);
    }

    /// <summary>The texts of a language as its .resw file has them.</summary>
    public static IReadOnlyDictionary<string, string> Resources(string language) => s_tables.GetOrAdd(language, tag =>
        XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Strings", tag, "Resources.resw")).Root!.Elements("data")
            .ToDictionary(d => (string)d.Attribute("name")!, d => (string)d.Element("value")!, StringComparer.Ordinal));
}
