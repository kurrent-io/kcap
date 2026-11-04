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

        string? violation;

        try {
            violation = AncestorChain.Violation(_root);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            violation = ex.Message;
        }

        if (violation is not null) throw new HeadlessRunRefusedException($"'{_root}' is not private: {violation}.");

        return CreatePrivate(Path.Combine(_root, $"{prefix}-{_epoch}-{Guid.NewGuid():N}"));
    }

    /// <summary>Removes the run directories of other epochs, which a crash left behind. Touches nothing
    /// unless the root is a real, owner-only directory on a private chain, and only entries named as
    /// <see cref="Create"/> names them. Returns why it did not sweep, or null.</summary>
    public string? SweepStale() {
        string? violation;

        try {
            violation = RootViolation();
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            violation = ex.Message;
        }

        if (violation is not null) return $"'{_root}' was not swept: {violation}.";

        IEnumerable<string> entries;

        try {
            entries = [.. Directory.EnumerateFileSystemEntries(_root)];
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return $"'{_root}' was not swept: {ex.Message}";
        }

        foreach (var entry in entries) {
            if (!IsRunName(Path.GetFileName(entry), out var epoch) || epoch == _epoch) continue;

            try {
                NoFollowTree.Delete(entry);
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                // Retried by the next start's sweep.
            }
        }

        return null;
    }

    string? RootViolation() {
        var attributes = new FileInfo(_root).Attributes;

        if ((int)attributes == -1) return "it does not exist";
        if (attributes.HasFlag(FileAttributes.ReparsePoint)) return "it is a link";
        if (!attributes.HasFlag(FileAttributes.Directory)) return "it is not a directory";

        var ownerOnly = OperatingSystem.IsWindows()
            ? OwnerOnlyDirectory.Violation(_root)
            : (File.GetUnixFileMode(_root) & GroupOrOther) != 0 ? "it is not mode 0700" : null;

        return ownerOnly ?? AncestorChain.Violation(_root);
    }

    static bool IsRunName(string name, out string epoch) {
        epoch = "";

        if (name.Split('-') is not [var prefix, var e, var guid]
         || prefix.Length == 0 || !prefix.All(char.IsAsciiLetterOrDigit)
         || guid.Length != 32 || !guid.All(char.IsAsciiHexDigitLower)) return false;

        epoch = e;
        return true;
    }

    const UnixFileMode GroupOrOther =
        UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
        UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

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
