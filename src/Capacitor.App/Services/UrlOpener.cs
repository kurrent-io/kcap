using System.Diagnostics;

namespace Capacitor.App.Services;

public interface IUrlOpener {
    void Open(string url);
}

/// UseShellExecute routes the URL through the OS shell handler (macOS `open`, Windows
/// ShellExecute, Linux xdg-open) instead of attempting to launch it as an executable.
public sealed class ShellUrlOpener : IUrlOpener {
    /// Process.Start can block while another spawn in the app is stalled, so the launch runs off the
    /// caller's thread. One that finishes within this wait still throws to the caller as before.
    static readonly TimeSpan CallerWait = TimeSpan.FromSeconds(1);

    public void Open(string url) {
        var launch = Task.Run(() => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose());
        if (Task.WaitAny([launch], CallerWait) == 0) {
            launch.GetAwaiter().GetResult();
            return;
        }

        // Neither the URL nor the message: a URL can carry credentials, and Windows puts it in the message.
        launch.ContinueWith(t => Console.Error.WriteLine($"kcap: opening a link failed: {t.Exception!.InnerException!.GetType().Name}"),
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
    }
}
