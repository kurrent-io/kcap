using Avalonia.Threading;

namespace Capacitor.App.ViewModels;

internal static class UiReads {
    public static Task Post(Action action) => Dispatcher.UIThread.InvokeAsync(action).GetTask();
}
