namespace Capacitor.Cli.Core.Processes;

/// <summary>Matches the names a process shows against the coding agent that owns it.</summary>
public static class AgentProcessNames {
    /// <summary>
    /// The names by which <paramref name="executable"/> could identify its agent: the basename,
    /// plus the directory above a <c>versions/</c> segment. Claude Code's native installer
    /// writes <c>~/.local/share/claude/versions/&lt;version&gt;</c>, where the basename is the
    /// version and only the grandparent names the agent.
    /// </summary>
    public static IReadOnlyList<string> Candidates(string? executable) {
        if (string.IsNullOrEmpty(executable)) {
            return [];
        }

        var parts = executable.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length == 0) {
            return [];
        }

        return parts.Length >= 3 && parts[^2].Equals("versions", StringComparison.OrdinalIgnoreCase)
            ? [parts[^1], parts[^3]]
            : [parts[^1]];
    }

    /// <summary>
    /// True when <paramref name="comm"/> names the coding-agent executable for
    /// <paramref name="vendor"/>. Matches the basename exactly (optionally plus a
    /// single file extension like <c>.exe</c>), case-insensitively — never a loose
    /// substring, so descriptive names such as the Electron desktop "Claude Helper
    /// (Renderer)" don't masquerade as the <c>claude</c> CLI.
    /// </summary>
    public static bool Matches(string comm, string vendor) {
        if (string.IsNullOrEmpty(comm)) {
            return false;
        }

        var slash    = comm.LastIndexOfAny(['/', '\\']);
        var basename = slash >= 0 ? comm[(slash + 1)..] : comm;

        // A versioned binary is matched by its prefix before the clean-stem gate, which its dots fail.
        if (VersionedBinaryPrefixOf(vendor) is { } prefix && basename.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) {
            return true;
        }

        // Reduce the basename to a clean stem: the whole name if it has no dot, or the part before a
        // single trailing extension (".exe"). Anything else (multiple dots, a space) is not a clean
        // executable name and never matches.
        var dot = basename.IndexOf('.');
        string stem;

        if (dot < 0) {
            stem = basename;
        } else if (dot > 0 && !basename.AsSpan(dot + 1).Contains('.') && !basename.AsSpan(dot + 1).Contains(' ')) {
            stem = basename[..dot];
        } else {
            return false;
        }

        // Match the vendor's process name OR `{name}-cli`. The bounded `-cli` tolerance fixes the Kiro
        // watchdog, whose durable process image is `kiro-cli` while its vendor token is `kiro`
        // The clean-stem gate above keeps it from over-matching unrelated processes.
        return ProcessNamesOf(vendor).Any(name =>
            stem.Equals(name, StringComparison.OrdinalIgnoreCase)
         || stem.Equals($"{name}-cli", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The names a vendor's process shows, where they are not the vendor token itself.
    /// Vibe retitles its interpreter to "Vibe CLI" on Linux and macOS; on Windows the title is
    /// unchanged and its <c>vibe.exe</c> launcher is what the walk meets.</summary>
    static string[] ProcessNamesOf(string vendor) => vendor switch {
        "mistral-vibe" => ["vibe", "Vibe CLI"],
        _              => [vendor],
    };

    /// <summary>The prefix of a vendor's versioned binary. Muse's <c>muse</c> is a launcher script that
    /// execs <c>muse-bin-&lt;version&gt;</c>, so the durable process never reads as <c>muse</c>; its
    /// comm is the same name truncated to 15 or 16 characters.</summary>
    static string? VersionedBinaryPrefixOf(string vendor) => vendor switch {
        "muse" => "muse-bin-",
        _      => null,
    };
}
