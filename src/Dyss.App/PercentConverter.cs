using Microsoft.UI.Xaml.Data;

namespace Dyss.App;

/// <summary>
/// A slider's value as a percentage, "20 %", for its thumb's tooltip: the one place x:Bind cannot
/// reach with a function (see <see cref="Ui"/>), the slider asking for a converter instead.
/// </summary>
public sealed partial class PercentConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        Ui.Percent((int)Math.Round(System.Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture)));

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
