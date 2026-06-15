using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using MediaColor = System.Windows.Media.Color;
using TrayIconManager.Models;

namespace TrayIconManager.Converters;

public class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool invert = parameter?.ToString() == "invert";
        bool boolVal = value is bool b && b;
        if (invert) boolVal = !boolVal;
        return boolVal ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is Visibility v && v == Visibility.Visible;
}

public class StatusToColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value?.ToString() == "可见"
            ? MediaColor.FromRgb(0x4C, 0xAF, 0x50)  // Green
            : MediaColor.FromRgb(0xFF, 0x98, 0x00);  // Orange
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotImplementedException();
}

public class MatchTypeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is MatchType mt)
        {
            return mt switch
            {
                MatchType.ProcessName => "进程名",
                MatchType.TooltipText => "提示文本",
                MatchType.Both => "两者皆可",
                _ => mt.ToString()
            };
        }
        return value?.ToString() ?? "";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value?.ToString() switch
        {
            "进程名" => MatchType.ProcessName,
            "提示文本" => MatchType.TooltipText,
            "两者皆可" => MatchType.Both,
            _ => MatchType.ProcessName
        };
    }
}

public class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is bool b && !b;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is bool b && !b;
}
