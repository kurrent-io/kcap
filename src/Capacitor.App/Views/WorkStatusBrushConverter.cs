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

    static string Key(WorkStatus? status) => status switch {
        WorkStatus.InProgress => "KcapPurpleBrush",
        WorkStatus.InReview   => "KcapInfoBrush",
        WorkStatus.Blocked    => "KcapWarningBrush",
        WorkStatus.Done       => "KcapSuccessBrush",
        WorkStatus.Open       => "KcapMutedBrush",
        _                     => "KcapFaintBrush",
    };

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Application.Current?.FindResource(Key(value as WorkStatus?)) as IBrush;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
