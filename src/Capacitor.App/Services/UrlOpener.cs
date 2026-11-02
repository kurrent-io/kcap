using System.Diagnostics;

namespace Capacitor.App.Services;

public interface IUrlOpener {
    void Open(string url);
}

/// UseShellExecute routes the URL through the OS shell handler (macOS `open`, Windows
/// ShellExecute, Linux xdg-open) instead of attempting to launch it as an executable. Off the
/// caller's thread: Process.Start can block while another spawn in the app is stalled.
public sealed class ShellUrlOpener : IUrlOpener {
    public void Open(string url) => _ = Task.Run(() => {
        try {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap: could not open {url}: {ex.Message}");
        }
    });
}
