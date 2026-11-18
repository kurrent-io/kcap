using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;

namespace Capacitor.App.Views;

/// A window's decoration margin without its title bar: the app paints the title bar itself, and the
/// frame on top is as thick as the one below.
public sealed class DrawnFrameInsetConverter : IValueConverter {
    public static readonly DrawnFrameInsetConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Thickness margin ? new Thickness(margin.Left, margin.Bottom, margin.Right, margin.Bottom) : value;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
