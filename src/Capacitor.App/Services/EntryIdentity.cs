using System.ComponentModel;
using Capacitor.Cli.Core;

namespace Capacitor.App.Services;

/// <summary>The entry itself, not what it points at: device, inode, and modification and change times, so a file
/// replaced at the same path reads differently.</summary>
internal sealed class EntryIdentity(IProcessRunner runner) {
    internal const string StatPath = "/usr/bin/stat";

    /// BSD stat reads the entry with lstat unless told to follow it.
    internal static readonly string[] StatArgs = ["-f", "%d:%i:%m:%c"];

    /// Null when the entry cannot be read, which includes one that is gone.
    public async Task<string?> OfAsync(string entry, CancellationToken ct) {
        try {
            var result = await runner.RunAsync(StatPath, [.. StatArgs, entry], new RunOptions(Timeout: TimeSpan.FromSeconds(5)), ct)
                .ConfigureAwait(false);
            return result is { ExitCode: 0, TimedOut: false } ? result.Stdout.Trim() : null;
        } catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException) {
            return null;
        }
    }
}
