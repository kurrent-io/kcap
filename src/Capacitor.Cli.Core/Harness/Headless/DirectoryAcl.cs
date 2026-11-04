namespace Capacitor.Cli.Core.Harness.Headless;

/// <summary>Whether a directory carries an extended ACL, read from the mode field of the system's own <c>ls</c>. With
/// a POSIX ACL the mode's group bits show the mask, so they say nothing about who can write.</summary>
internal static class DirectoryAcl {
    static readonly TimeSpan LsTimeout = TimeSpan.FromSeconds(5);

    /// <summary>System locations only, never PATH: a directory on PATH another account can write would let it answer.</summary>
    static readonly string[] Candidates = ["/bin/ls", "/usr/bin/ls", "/run/current-system/sw/bin/ls"];

    static readonly Dictionary<string, string> CLocale = new() { ["LC_ALL"] = "C" };

    /// <summary>True or false as <see cref="FromListing"/> reads it; null when that cannot tell or <c>ls</c> failed.</summary>
    public static bool? HasExtended(string directory) {
        if (Candidates.FirstOrDefault(File.Exists) is not { } ls) return null;

        try {
            var result = new ProcessRunner(TimeProvider.System)
                .RunAsync(ls, ["-ld", "--", directory], new RunOptions(EnvOverlay: CLocale, Timeout: LsTimeout), CancellationToken.None)
                .GetAwaiter().GetResult();

            return !result.TimedOut && result.ExitCode == 0 ? FromListing(result.Stdout) : null;
        } catch (Exception) {
            return null;
        }
    }

    /// <summary>Reads one <c>ls -ld</c> line of a directory. <c>+</c> is an ACL; nothing, or the <c>.</c> GNU prints for
    /// an SELinux context alone, is none. Anything else is null, <c>@</c> included: macOS prints it for extended
    /// attributes in place of the <c>+</c>, so it hides whether there is an ACL.</summary>
    public static bool? FromListing(string line) {
        var mode = line.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();

        if (mode is null || mode.Length < 10 || mode[0] != 'd') return null;

        for (var i = 1; i < 10; i++) {
            if (!IsModeChar(mode[i], i)) return null;
        }

        return mode[10..] switch {
            "" or "." => false,
            "+"       => true,
            _         => null,
        };
    }

    /// <summary>Positions 1 to 9 of the mode: read, write, then execute with its setuid, setgid or sticky forms.</summary>
    static bool IsModeChar(char c, int position) =>
        c == '-' || ((position - 1) % 3) switch {
            0 => c == 'r',
            1 => c == 'w',
            _ => c is 'x' or 's' or 'S' or 't' or 'T',
        };
}
