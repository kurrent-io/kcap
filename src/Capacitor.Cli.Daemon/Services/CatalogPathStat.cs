namespace Capacitor.Cli.Daemon.Services;

/// Fingerprint of a file a vendor's model catalog depends on. A missing file is a value of its
/// own, since deleting Pi's auth file does change what Pi can launch. Two fingerprints describe the
/// same catalog when their <see cref="Digest"/>s match, whatever the size and write time: a running
/// vendor rewrites its catalog and auth files with fresh timestamps and tokens and nothing else.
public readonly record struct CatalogPathStat(string Path, bool Exists, long Length, long LastWriteTicks, string? Digest = null) {
    /// <paramref name="previous"/>, when its size and write time still match, is returned as is,
    /// so an untouched file is never read. It is also what a file that exists but cannot be read
    /// right now reads as: a vendor holding it mid-rewrite is a transient, never a change.
    internal static CatalogPathStat Of(string path, CatalogPathStat? previous = null) {
        long length, ticks;
        try {
            var info = new FileInfo(path);
            if (!info.Exists) return new(path, false, 0, 0);
            length = info.Length;
            ticks  = info.LastWriteTimeUtc.Ticks;
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return new(path, false, 0, 0);
        }

        var prior = previous is { Exists: true, Digest: not null } p && p.Path == path ? p : (CatalogPathStat?) null;
        if (prior is { } same && same.Length == length && same.LastWriteTicks == ticks) return same;

        try {
            return new(path, true, length, ticks, CatalogContentDigest.Of(File.ReadAllBytes(path)));
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return prior ?? new(path, true, length, ticks);
        }
    }

    /// Two fingerprints without a digest compare by size and write time. One without a digest
    /// never matches one with: a file first read once it turns readable must still re-probe.
    internal bool SameCatalogAs(CatalogPathStat other) =>
        Path == other.Path && Exists == other.Exists
     && (!Exists || (Digest, other.Digest) switch {
            (null, null)         => Length == other.Length && LastWriteTicks == other.LastWriteTicks,
            (not null, not null) => Digest == other.Digest,
            _                    => false,
        });

    internal static bool SameCatalog(ReadOnlySpan<CatalogPathStat> current, ReadOnlySpan<CatalogPathStat> baseline) {
        if (current.Length != baseline.Length) return false;
        for (var i = 0; i < current.Length; i++)
            if (!current[i].SameCatalogAs(baseline[i])) return false;
        return true;
    }
}
