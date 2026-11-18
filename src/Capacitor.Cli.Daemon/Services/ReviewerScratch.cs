using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>The boot-time sweep shared by the per-daemon reviewer scratch roots.</summary>
internal static class ReviewerScratch {
    /// <summary>
    /// Hands <paramref name="delete"/> every directory in <paramref name="root"/> named with
    /// <paramref name="prefix"/> but not <paramref name="livePrefix"/>. Only safe because the root is this
    /// daemon's own: in a root shared with a peer, a name outside this epoch could be the peer's live launch.
    /// Never throws for an unreadable root, since it runs at daemon boot.
    /// </summary>
    internal static void SweepStale(string root, string prefix, string livePrefix, string kind,
                                    ILogger log, Action<string> delete) {
        if (!Directory.Exists(root)) return;

        IReadOnlyList<string> candidates;

        try {
            candidates = [.. Directory.EnumerateDirectories(root)];
        } catch (Exception ex) {
            log.LogWarning(ex, "Could not enumerate the {Kind} root {Root}; skipping the sweep", kind, root);
            return;
        }

        foreach (var dir in candidates) {
            var name = Path.GetFileName(dir);
            if (!name.StartsWith(prefix, StringComparison.Ordinal)) continue;
            if (name.StartsWith(livePrefix, StringComparison.Ordinal)) continue;

            delete(dir);
        }
    }
}
