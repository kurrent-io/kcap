using Capacitor.Cli.Core;

namespace Capacitor.App.Services;

/// <summary>Reads the outcome of an <c>osascript … with administrator privileges</c> run.</summary>
internal static class AdministratorPrompt {
    /// <summary>Whether the user dismissed macOS's administrator prompt: osascript then fails with error -128.</summary>
    public static bool WasCancelled(ProcessResult result) =>
        result.ExitCode != 0 && result.Stderr.Contains("(-128)", StringComparison.Ordinal);
}
