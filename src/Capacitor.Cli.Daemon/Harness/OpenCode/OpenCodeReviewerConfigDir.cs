using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Harness.Kiro;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Harness.OpenCode;

/// <summary>
/// The per-launch directories that keep an unattended OpenCode reviewer away from the operator's global
/// configuration, the analogue of <see cref="KiroReviewerHome"/>.
///
/// <para><b>Why they exist.</b> OpenCode reads the operator's global <c>mcp</c> servers and instructions into every
/// session. One server is the flows server, which would let a reviewer start nested review flows. OpenCode loads
/// global config from <c>$XDG_CONFIG_HOME/opencode</c>, <c>.opencode</c> under its home and
/// <c>OPENCODE_CONFIG_DIR</c>, and any one of them left at the operator's value brings the servers back.</para>
///
/// <para><b>Credentials are not under them.</b> They live in the data directory, which stays the user's.</para>
///
/// <para><b>Why not one shared set.</b> A future release that writes here would carry one reviewer's state into the
/// next, and a shared root reintroduces the peer-daemon problem <see cref="KiroReviewerHome"/> documents: "delete every
/// directory whose epoch is not mine" selects a peer daemon's live directory.</para>
///
/// <para><b>Emptiness is the mechanism, so it is verified.</b> Delete is best-effort (an undeletable directory must not
/// fail a round), so a partial failure could leave content behind and <c>CreateDirectory</c> would accept the
/// survivor.</para>
/// </summary>
internal static class OpenCodeReviewerConfigDir {
    const string Prefix = "kcap-opencode-reviewer-";

    /// <summary>This daemon's own reviewer-config root. Never shared with a peer daemon.</summary>
    internal static string RootFor(string stateDir) => Path.Combine(stateDir, "opencode-reviewers");

    /// <summary>One derivation of a directory's name. Create and delete both go through it, so a
    /// failed launch's cleanup cannot target a path the launch never made.</summary>
    internal static string NameFor(string daemonEpoch, string launchId) =>
        $"{Prefix}{Sanitize(daemonEpoch)}-{Sanitize(launchId)}";

    /// <summary>Creates the launch's empty, owner-only directories. Empty is what suppresses the operator's global
    /// configuration, so nothing may ever be seeded into them.</summary>
    internal static OpenCodeReviewerDirs Create(string stateDir, string daemonEpoch, string launchId, ILogger? log = null) {
        var root = RootFor(stateDir);
        CreateOwnerOnly(root);

        var dir = Path.Combine(root, NameFor(daemonEpoch, launchId));

        // CreateDirectory succeeds on an existing directory, so a repeated launch under the same epoch and agent id
        // would inherit what the previous one left.
        if (Path.Exists(dir)) Delete(dir, stateDir, log ?? NullLogger.Instance);

        CreateOwnerOnly(dir);

        return new(Empty(dir, "config"), Empty(dir, "home"), Empty(dir, "xdg-config"));
    }

    static string Empty(string parent, string name) {
        var dir = Path.Combine(parent, name);
        CreateOwnerOnly(dir);

        if (Directory.EnumerateFileSystemEntries(dir).Any())
            throw new InvalidOperationException(
                $"opencode_reviewer_config_dir_not_empty: '{dir}' still holds content from a previous "
              + "launch that could not be removed. Refusing rather than handing a reviewer the global "
              + "MCP servers an empty directory exists to suppress.");

        return dir;
    }

    /// <summary>
    /// Deletes every directory in THIS daemon's root whose epoch is not the current one — the crash and
    /// SIGKILL recovery. Safe by construction because the root is not shared.
    /// </summary>
    /// <para><b>Known ordering, accepted:</b> runs at daemon start, before the orphan-agent reaper —
    /// the same accepted residual <see cref="KiroReviewerHome.SweepStale"/> records.</para>
    internal static void SweepStale(string stateDir, string currentEpoch, ILogger? log = null) {
        log ??= NullLogger.Instance;
        var root = RootFor(stateDir);
        if (!Directory.Exists(root)) return;

        var live = $"{Prefix}{Sanitize(currentEpoch)}-";

        IReadOnlyList<string> candidates;

        try {
            candidates = [.. Directory.EnumerateDirectories(root)];
        } catch (Exception ex) {
            log.LogWarning(ex,
                "Could not enumerate the OpenCode reviewer config root {Root}; skipping the sweep", root);
            return;
        }

        foreach (var dir in candidates) {
            var name = Path.GetFileName(dir);
            if (!name.StartsWith(Prefix, StringComparison.Ordinal)) continue;
            if (name.StartsWith(live,   StringComparison.Ordinal)) continue;

            Delete(dir, stateDir, log);
        }
    }

    /// <summary>
    /// Removes a reviewer config dir. Never follows a link out of the tree, and refuses a path that
    /// does not resolve inside this daemon's root — the same two recursive-delete hazards
    /// <see cref="KiroReviewerHome.Delete"/> guards against, and it carries the same documented
    /// check-then-use residual for the same reason (closing it needs <c>unlinkat</c>-style handle
    /// semantics .NET does not expose, and the path lives under the daemon's own state directory).
    /// </summary>
    internal static void Delete(string dirPath, string stateDir, ILogger? log = null) {
        log ??= NullLogger.Instance;
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(RootFor(stateDir)));
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dirPath));

        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)) {
            log.LogWarning("OpenCode reviewer config dir {Path} resolves outside {Root}; refusing to delete",
                full, root);
            return;
        }

        try {
            NoFollowTree.Delete(full);
        } catch (Exception ex) {
            // Log and continue: an undeletable directory must not fail a round or block startup.
            log.LogWarning(ex, "Failed to delete OpenCode reviewer config dir {Path}", full);
        }
    }

    const UnixFileMode OwnerOnly =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    /// <summary>
    /// Creates a directory that is owner-only FROM ITS FIRST INSTANT, and verifies it. Not
    /// create-then-chmod: that leaves a window at the default umask.
    ///
    /// <para>Not best-effort either — same reasoning as <see cref="KiroReviewerHome"/>: a POSIX host
    /// that cannot achieve the mode must not run a reviewer. Note the honest scope of the claim here:
    /// unlike Kiro's home this directory is not currently observed to receive review CONTENT, so the
    /// mode protects against a future release that writes here rather than a measured leak today. It is
    /// enforced anyway because "empty" is a security property and a group-writable directory is one
    /// another local user could seed a config into.</para>
    /// </summary>
    static void CreateOwnerOnly(string path) {
        if (OperatingSystem.IsWindows()) {
            // Unreachable in production: OpenCodeReviewerCapability refuses Windows before any launch.
            // Kept total so a direct call in a test cannot silently create a world-writable directory.
            throw new PlatformNotSupportedException(
                "opencode_reviewer_unsupported_platform: a reviewer config dir cannot be created "
              + "owner-only here.");
        }

        Directory.CreateDirectory(path, OwnerOnly);

        // An existing directory keeps its own mode — CreateDirectory's mode argument applies only when
        // it creates. Verifying covers that, and any filesystem that ignores the request.
        var mode = File.GetUnixFileMode(path);

        if ((mode & (UnixFileMode.GroupRead  | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                     UnixFileMode.OtherRead  | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0)
            throw new InvalidOperationException(
                $"opencode_reviewer_config_dir_not_owner_only: '{path}' is mode {mode}. Another local "
              + "user could seed configuration — MCP servers included — into a directory whose "
              + "EMPTINESS is the reviewer's containment.");
    }

    static string Sanitize(string value) =>
        string.Concat(value.Select(c => char.IsLetterOrDigit(c) ? c : '_'));
}
