using System.Diagnostics;

namespace Capacitor.App.Services.Update;

public static class ProcessExecutable {
    /// The binary a running process was started from, or null when the process is gone or will not say.
    public static string? Of(int pid) {
        try {
            using var process = Process.GetProcessById(pid);
            return process.MainModule?.FileName;
        } catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException
                                         or System.ComponentModel.Win32Exception or UnauthorizedAccessException) {
            return null;
        }
    }
}
