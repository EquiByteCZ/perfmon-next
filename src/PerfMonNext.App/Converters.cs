using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using PerfMonNext.Core;

namespace PerfMonNext;

public sealed class HexBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var hex = value as string;
        if (string.IsNullOrWhiteSpace(hex))
            hex = "#4C9AFF";
        try
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            return brush;
        }
        catch
        {
            var fallback = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#4C9AFF"));
            fallback.Freeze();
            return fallback;
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class StatusBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var hex = value is MachineStatus status
            ? status switch
            {
                MachineStatus.Online => "#3DDC97",
                MachineStatus.Busy or MachineStatus.Timeout => "#E6B450",
                MachineStatus.Offline or MachineStatus.AccessDenied or MachineStatus.Error => "#F07178",
                _ => "#8B9CB0"
            }
            : "#8B9CB0";
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
}

public sealed class StringVisConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
