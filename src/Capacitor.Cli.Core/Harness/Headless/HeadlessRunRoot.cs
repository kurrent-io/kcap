namespace Capacitor.Cli.Core.Harness.Headless;

/// <summary>
/// The daemon-owned <c>headless-runs</c> directory under its state directory, holding one owner-only
/// directory per headless run, named <c>&lt;prefix&gt;-&lt;epoch&gt;-&lt;guid&gt;</c>. The root is verified
/// owner-only and its ancestor chain checked on every <see cref="Create"/>: the state directory can be
/// moved, and is only best-effort private.
/// </summary>
public sealed class HeadlessRunRoot(string stateDir, string daemonEpoch) {
    readonly string _root  = Path.Combine(stateDir, "headless-runs");
    readonly string _epoch = Sanitize(daemonEpoch);

    /// <summary>Throws <see cref="HeadlessRunRefusedException"/> when the root or the new directory cannot be
    /// verified private.</summary>
    public HeadlessRunDirectory Create(string prefix) {
        if (prefix.Length == 0 || !prefix.All(char.IsAsciiLetterOrDigit))
            throw new ArgumentException($"'{prefix}' is not an alphanumeric prefix.", nameof(prefix));

        Verify(_root);

        if (AncestorChain.Violation(_root) is { } violation)
            throw new HeadlessRunRefusedException($"'{_root}' is not private: {violation}.");

        return CreatePrivate(Path.Combine(_root, $"{prefix}-{_epoch}-{Guid.NewGuid():N}"));
    }

    /// <summary>Removes every entry not made under this daemon's epoch, which a crash left behind. Safe
    /// because the root belongs to one daemon's state directory.</summary>
    public void SweepStale() {
        IEnumerable<string> entries;

        try {
            entries = [.. Directory.EnumerateFileSystemEntries(_root)];
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return;
        }

        foreach (var entry in entries) {
            if (Path.GetFileName(entry).Split('-') is [_, var epoch, _] && epoch == _epoch) continue;

            try {
                NoFollowTree.Delete(entry);
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                // Retried by the next start's sweep.
            }
        }
    }

    internal static HeadlessRunDirectory CreatePrivate(string path) {
        Verify(path);

        if (Directory.EnumerateFileSystemEntries(path).Any())
            throw new HeadlessRunRefusedException($"'{path}' is not empty.");

        return new HeadlessRunDirectory(path);
    }

    static void Verify(string path) {
        try {
            OwnerOnlyDirectory.CreateVerified(path);
        } catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException) {
            throw new HeadlessRunRefusedException(ex.Message);
        }
    }

    static string Sanitize(string value) => string.Concat(value.Select(c => char.IsAsciiLetterOrDigit(c) ? c : '_'));
}
