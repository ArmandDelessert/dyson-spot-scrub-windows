using DyssCockpit.Core;
using Microsoft.Windows.ApplicationModel.Resources;

namespace DyssCockpit.App.Services;

/// <summary>
/// The texts of the code, from the resources of the application (Strings\{language}\Resources.resw), in the language Windows
/// picks for it: the one chosen in the settings (see <see cref="Program"/>) or, by default, the one of Windows. The texts of
/// the XAML are those of the resources named by each element's <c>x:Uid</c>, loaded by the framework.
/// </summary>
public sealed class ResourceStrings : IStringSource
{
    private readonly ResourceLoader _strings = new();

    public string? Find(string key)
    {
        var value = _strings.GetString(key);
        return string.IsNullOrEmpty(value) ? null : value;
    }
}
