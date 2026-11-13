using Capacitor.Cli.Core;

namespace Capacitor.Client;

/// Selects the sessions of a set of git roots. A repository with an origin remote is matched by its
/// group key; one without is matched by path, because a server-listed row for it carries a hashed
/// `repo:local~…` key that never equals the local `path:` key.
/// A path names a directory only on its own machine, so a row from another machine matches by key alone. A key
/// mismatch cannot rule out a row recorded here: a renamed repository keeps its old name on sessions already recorded.
public sealed class ProjectScope {
    const string LocalRepositoryKeyPrefix = "repo:" + LocalRepository.OwnerPrefix;

    readonly HashSet<string> _keys;
    readonly string[] _roots;
    readonly string? _machineId;

    ProjectScope(HashSet<string> keys, string[] roots, string? machineId) {
        _keys = keys;
        _roots = roots;
        _machineId = machineId;
    }

    public bool IsEmpty => _roots.Length == 0;

    /// <param name="localMachineId">This machine's id. Without one, no server row is known to be recorded here.</param>
    public static ProjectScope For(
            IReadOnlyList<string> gitRoots, RepoIdentityResolver resolver, Func<string, string> resolveMainRoot, string? localMachineId) {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var roots = new HashSet<string>(PlatformPaths.Comparer);

        foreach (var given in gitRoots) {
            var root = Canonical(given);
            if (string.IsNullOrWhiteSpace(root)) continue;

            var main = Canonical(resolveMainRoot(root));
            roots.Add(root);
            roots.Add(main);

            var key = resolver.ForLocalRoot(main).Key;
            if (key.StartsWith("repo:", StringComparison.Ordinal)) keys.Add(key);
        }

        return new ProjectScope(keys, [.. roots], string.IsNullOrEmpty(localMachineId) ? null : localMachineId);
    }

    public bool Matches(AgentRow row) {
        if (IsEmpty) return false;
        if (_keys.Contains(row.RepoGroupKey)) return true;

        return MayMatchByPath(row) && (LiesUnderARoot(row.RepoPath) || LiesUnderARoot(row.WorktreePath) || LiesUnderARoot(row.BorrowedFrom));
    }

    /// A server row names no recording machine when the server is older or the viewer does not own the session. It is
    /// presumed to be from here unless it names a repository with an origin that is not one of the scope's.
    bool MayMatchByPath(AgentRow row) => row.Origin switch {
        AgentOrigin.Local or AgentOrigin.Pending => true,
        AgentOrigin.Server when _machineId is not null && row.Recording?.MachineId is { Length: > 0 } => row.RecordedOn(_machineId),
        AgentOrigin.Server => !NamesAnOriginRepository(row.RepoGroupKey),
        _ => false,
    };

    static bool NamesAnOriginRepository(string key) =>
        key.StartsWith("repo:", StringComparison.Ordinal) && !key.StartsWith(LocalRepositoryKeyPrefix, StringComparison.Ordinal);

    // A client may write Windows paths with forward slashes. Elsewhere a backslash is a file-name character.
    static string Canonical(string? path) {
        if (string.IsNullOrEmpty(path)) return "";

        return PlatformPaths.Normalize(OperatingSystem.IsWindows() ? path.Replace('/', '\\') : path);
    }

    bool LiesUnderARoot(string? path) {
        if (string.IsNullOrEmpty(path)) return false;

        for (var dir = Canonical(path); !string.IsNullOrEmpty(dir); dir = Path.GetDirectoryName(dir))
            foreach (var root in _roots)
                if (PlatformPaths.Comparer.Equals(dir, root)) return true;

        return false;
    }
}
