using Capacitor.Cli.Core;

namespace Capacitor.Cli.Daemon.Acp;

/// <summary>The repository a hosted session runs in: the origin of its worktree's main checkout, and the
/// branch the worktree has out. A repository whose origin names no owner is named by its
/// <see cref="LocalRepository"/> identity and has no remote URL.</summary>
public readonly record struct HostedSessionRepository(string Owner, string Name, string? RemoteUrl, string? Branch) {
    /// <summary>Null outside a repository.</summary>
    /// <param name="localIdentity">The <see cref="LocalRepository"/> identity of a directory, asked only when
    /// the origin names no owner.</param>
    public static HostedSessionRepository? Read(string? cwd, Func<string, (string Owner, string Name)?> localIdentity) {
        // The shared git directory holds the config for a main checkout, every linked worktree of it,
        // and a submodule, whose own lives under the superproject's .git/modules/.
        if (string.IsNullOrEmpty(cwd) || GitRepository.ResolveCommonDir(cwd) is not { } commonDir) return null;

        var url = GitRemoteReader.ReadOriginUrlFromConfig(Path.Combine(commonDir, "config"));

        var (owner, name) = GitUrlParser.ParseRemoteUrl(url);
        if (!string.IsNullOrEmpty(owner) && !string.IsNullOrEmpty(name))
            return new HostedSessionRepository(owner, name, url, GitRepository.CurrentBranch(cwd));

        return localIdentity(cwd) is { } local
            ? new HostedSessionRepository(local.Owner, local.Name, null, GitRepository.CurrentBranch(cwd))
            : null;
    }
}
