namespace Capacitor.Cli.Daemon.Services;

/// Fingerprint of a file a vendor's model catalog depends on. A missing file is a value of its
/// own, since deleting Pi's auth file does change what Pi can launch. Two fingerprints describe the
/// same catalog when their <see cref="Digest"/>s match, whatever the size and write time: a running
/// vendor rewrites its catalog and auth files with fresh timestamps and tokens and nothing else.
public readonly record struct CatalogPathStat(string Path, bool Exists, long Length, long LastWriteTicks, string? Digest = null) {
    /// <paramref name="previous"/>, when its size and write time still match, is returned as is,
    /// so an untouched file is never read. Its digest is kept for a file that cannot be read right
    /// now, since a vendor holding it mid-rewrite is a transient, never a change, and for a write
    /// <paramref name="writtenByProbedCli"/> refuses.
    internal static CatalogPathStat Of(
            string path, CatalogPathStat? previous = null, Func<string, byte[], bool>? writtenByProbedCli = null) {
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

        byte[] content;
        try {
            content = ReadShared(path);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return prior ?? new(path, true, length, ticks);
        }

        // The new size and write time are kept, so the next tick does not read the file again.
        if (prior is { } kept && writtenByProbedCli?.Invoke(path, content) == false)
            return kept with { Length = length, LastWriteTicks = ticks };
        return new(path, true, length, ticks, CatalogContentDigest.Of(content));
    }

    // The vendor owns the file: a Windows read that denies writers would stall its rewrite.
    static byte[] ReadShared(string path) {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
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
