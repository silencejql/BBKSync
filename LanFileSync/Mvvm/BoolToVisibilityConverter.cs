using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace LanFileSync.Mvvm;

/// <summary>bool → Visibility：true 显示，false 折叠。</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is Visibility.Visible;
}
