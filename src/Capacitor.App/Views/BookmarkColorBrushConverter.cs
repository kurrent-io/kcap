using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Capacitor.App.ViewModels;

namespace Capacitor.App.Views;

/// A bookmark's colour as its palette brush, `KcapBookmark{Color}Brush`.
public sealed class BookmarkColorBrushConverter : IValueConverter {
    public static readonly BookmarkColorBrushConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is ChatBookmarkColor color && Application.Current?.TryFindResource($"KcapBookmark{color}Brush", out var brush) == true
            ? brush as IBrush
            : Brushes.Gray;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
