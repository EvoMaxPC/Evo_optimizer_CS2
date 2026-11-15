using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace FragForge.App;

public sealed class SingleLineTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is string text ? text.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ').Replace('\u0085', ' ').Replace('\u2028', ' ').Replace('\u2029', ' ') : "";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => DependencyProperty.UnsetValue;
}
