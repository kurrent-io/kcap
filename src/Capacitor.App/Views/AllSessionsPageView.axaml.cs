using Avalonia.Controls;
using Avalonia.Threading;

namespace Capacitor.App.Views;

/// The sessions page drawn over the main area. DataContext is its AllSessionsPageViewModel; the arrow keys move
/// between the search box and the rows.
public partial class AllSessionsPageView : UserControl {
    public AllSessionsPageView() {
        InitializeComponent();
        DataContextChanged += (_, _) => {
            if (DataContext is not null) Dispatcher.UIThread.Post(() => AllSessionsSearch.Focus(), DispatcherPriority.Loaded);
        };
    }
}
