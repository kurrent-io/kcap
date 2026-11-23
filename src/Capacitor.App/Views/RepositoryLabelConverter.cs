using System.Globalization;
using Avalonia.Data.Converters;
using Capacitor.App.ViewModels;

namespace Capacitor.App.Views;

/// RepositoryChip's label: the repository's leaf, beside a folder icon.
public sealed class RepositoryLabelConverter : IValueConverter {
    public static readonly RepositoryLabelConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string { Length: > 0 } path ? RepoLabel.Leaf(path) : "Choose a repository";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
