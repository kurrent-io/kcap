namespace Capacitor.Cli.Daemon.Services;

/// Fingerprint of a file a vendor's model catalog depends on. A missing file is a value of its
/// own, since deleting Pi's auth file does change what Pi can launch. Two fingerprints describe the
/// same catalog when their <see cref="Digest"/>s match, whatever the size and write time: a running
/// vendor rewrites its catalog and auth files with fresh timestamps and tokens and nothing else.
public readonly record struct CatalogPathStat(string Path, bool Exists, long Length, long LastWriteTicks, string? Digest = null) {
    /// <paramref name="previous"/>, when its size and write time still match, is returned as is,
    /// so an untouched file is never read.
    internal static CatalogPathStat Of(string path, CatalogPathStat? previous = null) {
        try {
            var info = new FileInfo(path);
            if (!info.Exists) return new(path, false, 0, 0);

            var length = info.Length;
            var ticks  = info.LastWriteTimeUtc.Ticks;
            if (previous is { Exists: true, Digest: not null } p && p.Path == path && p.Length == length && p.LastWriteTicks == ticks)
                return p;

            return new(path, true, length, ticks, CatalogContentDigest.Of(File.ReadAllBytes(path)));
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return new(path, false, 0, 0);
        }
    }

    /// A fingerprint without a digest compares by size and write time.
    internal bool SameCatalogAs(CatalogPathStat other) =>
        Path == other.Path && Exists == other.Exists
     && (!Exists || (Digest is not null && other.Digest is not null
            ? Digest == other.Digest
            : Length == other.Length && LastWriteTicks == other.LastWriteTicks));

    internal static bool SameCatalog(ReadOnlySpan<CatalogPathStat> current, ReadOnlySpan<CatalogPathStat> baseline) {
        if (current.Length != baseline.Length) return false;
        for (var i = 0; i < current.Length; i++)
            if (!current[i].SameCatalogAs(baseline[i])) return false;
        return true;
    }
}
