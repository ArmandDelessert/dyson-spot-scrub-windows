using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Dyss.App.Rendering;
using Dyss.Presentation.Map;

namespace Dyss.App;

public sealed class NotConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is bool b ? !b : value;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is bool b ? !b : value;
}

/// <summary>true or a non-empty text → Visible; false, null or "" → Collapsed. A text can then carry its own visibility, as a notice that is only there when it says something.</summary>
public sealed class ShowConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is true or string { Length: > 0 } ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>true → Collapsed, false → Visible.</summary>
public sealed class CollapseConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is true ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>A view model's <see cref="ArgbColor"/> → a frozen brush of that colour.</summary>
public sealed class ColorBrushConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not ArgbColor c) return null;
        var brush = new SolidColorBrush(c.ToWpf());
        brush.Freeze();
        return brush;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
