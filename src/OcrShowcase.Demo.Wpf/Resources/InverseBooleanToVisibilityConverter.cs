using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace OcrShowcase.Demo.Wpf.Resources;

public sealed class InverseBooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var visible = value is bool boolValue && boolValue;
        return visible ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is Visibility visibility && visibility != Visibility.Visible;
    }
}
