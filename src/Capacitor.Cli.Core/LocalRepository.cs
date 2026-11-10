using System.Security.Cryptography;
using System.Text;

namespace Capacitor.Cli.Core;

/// <summary>
/// The identity of a git repository with no remote to name it: an owner hashed from this machine's id and
/// the repository's own git directory, and the folder's name.
/// <para>The owner is opaque because the server keys repositories by owner and name across every user of a
/// tenant: a shared owner would merge unrelated repositories that happen to share a folder name, and a path
/// would hand one person's directory layout to everyone who can see the repository.</para>
/// <para>The server recognizes these by the owner's prefix alone, so the prefix is a wire contract with it.
/// No hosting service allows <c>~</c> in an account name, so no remote can produce one.</para>
/// </summary>
public static class LocalRepository {
    public const string OwnerPrefix = "local~";

    /// <summary>The allow and deny list entry that names every local repository with this folder name.</summary>
    public const string FolderEntryPrefix = OwnerPrefix + "/";

    const int HashLength = 20;

    public static bool IsLocal(string? owner) => owner?.StartsWith(OwnerPrefix, StringComparison.OrdinalIgnoreCase) == true;

    public static string Label(string? owner, string? name) => IsLocal(owner) ? $"{name} (local)" : $"{owner}/{name}";

    /// <summary>
    /// The identity of the repository holding <paramref name="startDir"/>, or null outside one or when this
    /// machine's id cannot be read or created. Every working tree of one repository shares it, because they
    /// share a git directory; moving that directory, or reading it on another machine, makes another one.
    /// </summary>
    public static (string Owner, string Name)? Of(ConfigRoot config, string startDir) {
        try {
            if (GitRepository.FindRoot(startDir) is not { } root) return null;
            if (GitRepository.ResolveCommonDir(startDir) is not { } commonDir) return null;
            if (NameOf(commonDir, root) is not { Length: > 0 } name) return null;

            var machine = new MachineId(config).Get();
            var digest  = SHA256.HashData(Encoding.UTF8.GetBytes($"{machine}\n{PathComparison.Key(commonDir)}"));

            return (OwnerPrefix + Convert.ToHexStringLower(digest)[..HashLength], name);
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) {
            return null;
        }
    }

    /// <summary>The folder a person knows the repository by: the main checkout's, which a linked worktree
    /// shares; a bare repository's without its <c>.git</c>; otherwise the working tree's own.</summary>
    internal static string? NameOf(string commonDir, string workRoot) {
        var trimmed = commonDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var leaf    = Path.GetFileName(trimmed);

        if (leaf.Equals(".git", StringComparison.OrdinalIgnoreCase)) return Path.GetFileName(Path.GetDirectoryName(trimmed));
        if (leaf.EndsWith(".git", StringComparison.OrdinalIgnoreCase) && leaf.Length > 4) return leaf[..^4];

        return Path.GetFileName(workRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
    }

    /// <summary>Whether a list entry names this local repository: by its full key, or by
    /// <c>local~/&lt;folder&gt;</c>, which names every local repository on the machine with that folder name
    /// and nothing with a remote.</summary>
    public static bool Matches(string entry, string repoKey) {
        if (entry.Equals(repoKey, StringComparison.OrdinalIgnoreCase)) return true;
        if (!entry.StartsWith(FolderEntryPrefix, StringComparison.OrdinalIgnoreCase)) return false;

        var slash = repoKey.IndexOf('/');

        return slash > 0
            && IsLocal(repoKey[..slash])
            && repoKey[(slash + 1)..].Equals(entry[FolderEntryPrefix.Length..], StringComparison.OrdinalIgnoreCase);
    }
}
