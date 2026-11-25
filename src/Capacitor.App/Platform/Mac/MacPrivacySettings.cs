using System.Diagnostics;

namespace Capacitor.App.Platform.Mac;

/// Files and Folders is where macOS lifts a privacy denial for the daemon. A folder picker in the
/// app cannot: what it grants belongs to the app's process, and the daemon is a separate launchd job.
internal static class MacPrivacySettings {
    const string FilesAndFolders = "x-apple.systempreferences:com.apple.preference.security?Privacy_FilesAndFolders";

    public static void OpenFilesAndFolders() =>
        // Off the caller's thread: Process.Start can block while another spawn in the app is stalled.
        _ = Task.Run(() => {
            try {
                using var process = Process.Start(new ProcessStartInfo("/usr/bin/open") { ArgumentList = { FilesAndFolders }, UseShellExecute = false });
            } catch (Exception ex) {
                Console.Error.WriteLine($"kcap: opening Privacy settings failed: {ex.Message}");
            }
        });
}
