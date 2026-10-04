using Dyss.Core;
using Microsoft.UI.Xaml.Markup;

namespace Dyss.App;

/// <summary>
/// A text of the XAML in both languages, side by side like <see cref="Translation.T"/> in code:
/// <c>Text="{local:Tr Fr='Tableau de bord', En=Dashboard}"</c>. Values holding a comma go in
/// single quotes, and an apostrophe inside quotes is written <c>\'</c>.
/// </summary>
[MarkupExtensionReturnType(ReturnType = typeof(string))]
public sealed partial class Tr : MarkupExtension
{
    public string Fr { get; set; } = "";
    public string En { get; set; } = "";

    protected override object ProvideValue() => Translation.T(Fr, En);
}
