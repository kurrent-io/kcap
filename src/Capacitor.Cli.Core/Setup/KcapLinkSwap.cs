namespace Capacitor.Cli.Core.Setup;

/// Replaces a <c>kcap</c> entry by renaming a link made beside it over it, so a <c>PATH</c> lookup finds either the
/// old entry or the new link, never nothing. A folder this user cannot write throws
/// <see cref="UnauthorizedAccessException"/>, which a caller with an administrator route tells apart from a changed
/// entry.
public static class KcapLinkSwap {
    /// Throws <see cref="IOException"/>, changing nothing, when <paramref name="path"/> no longer links to
    /// <paramref name="expected"/>, checked again once the staging link exists.
    public static void Replace(string path, string expected, string replacement) {
        StillLinks(path, expected);
        var staging = Stage(path, replacement);
        try {
            StillLinks(path, expected);
            File.Move(staging, path, overwrite: true);
        } finally {
            Discard(staging);
        }
    }

    /// For an entry that need not be a link: <paramref name="unchanged"/> runs once the staging link exists, right
    /// before the rename, and false leaves the entry as it is.
    public static async Task<bool> ReplaceAsync(
            string path, string replacement, Func<CancellationToken, Task<bool>> unchanged, CancellationToken ct) {
        var staging = Stage(path, replacement);
        try {
            if (!await unchanged(ct).ConfigureAwait(false)) return false;
            File.Move(staging, path, overwrite: true);
            return true;
        } finally {
            Discard(staging);
        }
    }

    static void StillLinks(string path, string expected) {
        if (new FileInfo(path).LinkTarget != expected) throw new IOException($"{path} no longer links to {expected}.");
    }

    static string Stage(string path, string replacement) {
        var staging = Path.Join(Path.GetDirectoryName(path), $".{Path.GetFileName(path)}-{Guid.NewGuid():N}");
        File.CreateSymbolicLink(staging, replacement);
        return staging;
    }

    static void Discard(string staging) {
        try {
            if (new FileInfo(staging).LinkTarget is not null) File.Delete(staging);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            Console.Error.WriteLine($"kcap: could not remove temporary link {staging}: {ex.Message}");
        }
    }
}
