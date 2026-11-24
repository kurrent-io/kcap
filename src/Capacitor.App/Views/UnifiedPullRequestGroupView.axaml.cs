using Avalonia;
using Avalonia.Controls;
using Capacitor.App.ViewModels;

namespace Capacitor.App.Views;

public sealed partial class UnifiedPullRequestGroupView : UserControl {
    public UnifiedPullRequestGroupView() => InitializeComponent();

    internal Control Heading => GroupHeading;

    internal Control? FindRow(string? key) {
        if (key is null) return null;
        return Search(GroupRows) ?? Search(PassedRows);

        Control? Search(ItemsControl items) {
            if (!items.IsEffectivelyVisible) return null;
            for (var index = 0; index < items.ItemCount; index++)
                if (items.Items[index] is UnifiedPullRequestItem item && item.Row.Id == key)
                    return items.ContainerFromIndex(index);
            return null;
        }
    }

    internal Control? RowAtTop(ScrollViewer viewport) => Find(PassedRows, viewport) ?? Find(GroupRows, viewport);

    static Control? Find(ItemsControl items, ScrollViewer viewport) {
        if (!items.IsEffectivelyVisible || items.TranslatePoint(default, viewport) is not { Y: <= 0 }) return null;
        var low = 0;
        var high = items.ItemCount - 1;
        Control? candidate = null;
        while (low <= high) {
            var middle = low + (high - low) / 2;
            if (items.ContainerFromIndex(middle) is not { } row) break;
            if (row.TranslatePoint(default, viewport) is { Y: <= 0 }) {
                candidate = row;
                low = middle + 1;
            } else high = middle - 1;
        }
        return candidate;
    }

    internal static string? RowKey(Control? row) => (row?.DataContext as UnifiedPullRequestItem)?.Row.Id;
}
