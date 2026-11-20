using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Capacitor.App.ViewModels;

namespace Capacitor.App.Views;

/// Status → brush for a work item's parts. No status is the On the work item group.
public sealed class WorkStatusBrushConverter : IValueConverter {
    public static readonly WorkStatusBrushConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Application.Current?.FindResource(value is WorkStatus status ? status.BrushKey() : "KcapFaintBrush") as IBrush;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
