using Avalonia.Controls;
using Capacitor.App.ViewModels;
using Capacitor.App.Views;

namespace Capacitor.App.Services;

public sealed class LaunchApprovalsCoordinator(ActivityViewModel activity) : IDisposable {
    bool _disposed;

    public LaunchApprovalsWindow? Window { get; private set; }

    public void Show() {
        if (_disposed) return;
        if (Window is null) {
            var window = new LaunchApprovalsWindow { DataContext = activity };
            window.Closed += (_, _) => Window = null;
            Window = window;
        }
        if (Window.WindowState == WindowState.Minimized) Window.WindowState = WindowState.Normal;
        Window.Show();
        Window.Activate();
    }

    public void Dispose() {
        _disposed = true;
        Window?.Close();
        Window = null;
    }
}
