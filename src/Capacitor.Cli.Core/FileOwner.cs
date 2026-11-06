using System.Globalization;
using System.Runtime.InteropServices;

namespace Capacitor.Cli.Core;

/// <summary>Which account owns a path. No BCL API exposes a file's owner, and <c>struct stat</c> differs by
/// platform, architecture and libc, so the owner comes from the system's own <c>stat</c> command rather than
/// a hand-marshalled struct that could misread it on one target.</summary>
public static partial class FileOwner {
    static readonly TimeSpan StatTimeout = TimeSpan.FromSeconds(5);

    /// <summary>System locations only, never PATH: a directory on PATH another account can write would
    /// let it answer for every owner. NixOS keeps its coreutils under <c>/run/current-system</c>.</summary>
    static readonly string[] Candidates = ["/usr/bin/stat", "/bin/stat", "/run/current-system/sw/bin/stat"];

    /// <summary>The first candidate that reads <c>/</c> as root's, the one owner every system agrees on.
    /// A file that exists but cannot do that is no reader at all.</summary>
    static readonly Lazy<string?> Stat = new(() => Candidates.FirstOrDefault(c => File.Exists(c) && Read(c, "u", "/") == 0));

    public static uint Effective => geteuid();

    /// <summary>Whether owners can be read on this system at all. False on Windows, which has no owner
    /// model this audit uses, and where no system <c>stat</c> reads <c>/</c> correctly.</summary>
    public static bool Available => !OperatingSystem.IsWindows() && Stat.Value is not null;

    /// <summary>The owning uid, or null when it cannot be read: on Windows, without a working
    /// <c>stat</c>, or for a path this process cannot stat.</summary>
    public static uint? Of(string path) => Available ? Read(Stat.Value!, "u", path) : null;

    /// <summary>The owning gid, null under the same conditions as <see cref="Of"/>.</summary>
    public static uint? GroupOf(string path) => Available ? Read(Stat.Value!, "g", path) : null;

    static uint? Read(string stat, string field, string path) {
        try {
            var result = new ProcessRunner(TimeProvider.System)
                .RunAsync(stat, [OperatingSystem.IsMacOS() ? "-f" : "-c", $"%{field}", path],
                    new RunOptions(Timeout: StatTimeout), CancellationToken.None)
                .GetAwaiter().GetResult();

            return !result.TimedOut && result.ExitCode == 0
                && uint.TryParse(result.Stdout.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var uid)
                ? uid
                : null;
        } catch (Exception) {
            return null;
        }
    }

    [LibraryImport("libc", EntryPoint = "geteuid")]
    private static partial uint geteuid();
}
