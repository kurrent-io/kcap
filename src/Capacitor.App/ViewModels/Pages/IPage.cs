namespace Capacitor.App.ViewModels.Pages;

/// A page drawn over the main area, the workspace kept open beneath it. Disposed when it closes.
public interface IPage : IDisposable {
    PageKey Key { get; }
}
