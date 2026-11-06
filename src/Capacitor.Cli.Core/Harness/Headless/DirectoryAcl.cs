using System.Collections.Frozen;

namespace Capacitor.Cli.Core.Harness.Headless;

/// <summary>What a directory's ACL allows, read from the system's own <c>ls</c>. With a POSIX ACL the mode's group bits
/// show the mask, so they say nothing about who can write; a macOS ACL is independent of the mode altogether.</summary>
internal static class DirectoryAcl {
    static readonly TimeSpan LsTimeout = TimeSpan.FromSeconds(5);

    /// <summary>System locations only, never PATH: a directory on PATH another account can write would let it answer.</summary>
    static readonly string[] Candidates = ["/bin/ls", "/usr/bin/ls", "/run/current-system/sw/bin/ls"];

    static readonly Dictionary<string, string> CLocale = new() { ["LC_ALL"] = "C" };

    /// <summary>Rights that can replace an entry below a directory or take control of it, on either spelling
    /// <c>chmod</c> and <c>ls</c> use for a directory.</summary>
    static readonly FrozenSet<string> ReplaceRights = FrozenSet.ToFrozenSet(
        ["write", "delete", "delete_child", "add_file", "add_subdirectory", "append", "writesecurity", "chown"], StringComparer.Ordinal);

    static readonly FrozenSet<string> OtherRights = FrozenSet.ToFrozenSet(
        ["read", "list", "execute", "search", "readattr", "writeattr", "readextattr", "writeextattr", "readsecurity",
         "file_inherit", "directory_inherit", "limit_inherit", "only_inherit"], StringComparer.Ordinal);

    /// <summary>True or false as <see cref="FromListing"/> reads it; null when that cannot tell or <c>ls</c> failed.</summary>
    public static bool? HasExtended(string directory) => Ls("-ld", directory) is { } listing ? FromListing(listing) : null;

    /// <summary>Why a named ACL on <paramref name="directory"/> lets someone else replace what it holds, or null. Only
    /// macOS asks: its ACLs are independent of the mode bits, while a POSIX ACL's mask shows in the group bits, which the
    /// write rule reads.</summary>
    public static string? AncestorViolation(string directory) {
        if (!OperatingSystem.IsMacOS()) return null;

        return Ls("-lde", directory) is { } listing
            ? MacListingViolation(listing, ["user:root", $"user:{Environment.UserName}"]) is { } why ? $"'{directory}': {why}" : null
            : $"the ACL of '{directory}' cannot be read";
    }

    static string? Ls(string flags, string directory) {
        if (Candidates.FirstOrDefault(File.Exists) is not { } ls) return null;

        try {
            var result = new ProcessRunner(TimeProvider.System)
                .RunAsync(ls, [flags, "--", directory], new RunOptions(EnvOverlay: CLocale, Timeout: LsTimeout), CancellationToken.None)
                .GetAwaiter().GetResult();

            return !result.TimedOut && result.ExitCode == 0 ? result.Stdout : null;
        } catch (Exception) {
            return null;
        }
    }

    /// <summary>Reads a macOS <c>ls -lde</c> listing: the entry's own line, then one <c>N: principal [inherited]
    /// allow|deny rights</c> line per ACL entry. Deny entries only narrow, so they pass; the default home directory
    /// has <c>group:everyone deny delete</c>. A line or right it cannot read is a violation, so that nothing unread
    /// passes. A principal <c>ls</c> cannot name prints as a UUID, which is not one of
    /// <paramref name="trusted"/>.</summary>
    public static string? MacListingViolation(string listing, IReadOnlyCollection<string> trusted) {
        var lines = listing.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        if (lines.Length == 0 || !IsModeField(lines[0].Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(), "dl"))
            return "its listing cannot be read";

        foreach (var line in lines.Skip(1)) {
            var entry = line.TrimStart();
            var colon = entry.IndexOf(": ", StringComparison.Ordinal);

            if (colon <= 0 || !entry[..colon].All(char.IsAsciiDigit)) return $"its ACL line '{entry}' cannot be read";

            // Rights hold no spaces and a principal can, so this reads from the right.
            var words = entry[(colon + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);

            if (words.Length < 3 || words[^2] is not ("allow" or "deny")) return $"its ACL line '{entry}' cannot be read";
            if (words[^2] == "deny") continue;

            var rights = words[^1].Split(',');

            if (rights.FirstOrDefault(r => !ReplaceRights.Contains(r) && !OtherRights.Contains(r)) is { } unknown)
                return $"its ACL names a right '{unknown}' that cannot be judged";

            var named     = words[^3] == "inherited" ? words[..^3] : words[..^2];
            var principal = string.Join(' ', named);

            if (principal.Length == 0) return $"its ACL line '{entry}' cannot be read";
            if (trusted.Contains(principal, StringComparer.Ordinal)) continue;

            if (rights.FirstOrDefault(ReplaceRights.Contains) is { } right)
                return $"its ACL allows {principal} {right}";
        }

        return null;
    }

    /// <summary>Reads one <c>ls -ld</c> line of a directory. <c>+</c> is an ACL; nothing, or the <c>.</c> GNU prints for
    /// an SELinux context alone, is none. Anything else is null, <c>@</c> included: macOS prints it for extended
    /// attributes in place of the <c>+</c>, so it hides whether there is an ACL.</summary>
    public static bool? FromListing(string line) {
        var mode = line.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();

        if (!IsModeField(mode, "d")) return null;

        return mode![10..] switch {
            "" or "." => false,
            "+"       => true,
            _         => null,
        };
    }

    static bool IsModeField(string? mode, string kinds) {
        if (mode is null || mode.Length < 10 || !kinds.Contains(mode[0], StringComparison.Ordinal)) return false;

        for (var i = 1; i < 10; i++) {
            if (!IsModeChar(mode[i], i)) return false;
        }

        return true;
    }

    /// <summary>Positions 1 to 9 of the mode: read, write, then execute with its setuid, setgid or sticky forms.</summary>
    static bool IsModeChar(char c, int position) =>
        c == '-' || ((position - 1) % 3) switch {
            0 => c == 'r',
            1 => c == 'w',
            _ => c is 'x' or 's' or 'S' or 't' or 'T',
        };
}
