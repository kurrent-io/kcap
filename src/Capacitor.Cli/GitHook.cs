using Capacitor.Cli.Recording;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Mcp;
using Capacitor.Cli.Recording.PrDetection;

namespace Capacitor.Cli;

/// <summary>
/// <c>kcap git-hook</c>, which git runs after a commit anywhere on this machine: it files the commit
/// under the session of the coding agent above it.
/// </summary>
static class GitHook {
    static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// The <see cref="CommitObservation"/> a watcher starts with.
    /// </summary>
    public static Task<CommitObservation> ObservationAsync(ConfigRoot config, string sessionId, string? agentId, string? cwd, TimeProvider time) =>
        GitHookProbe.ObservationAsync(config, sessionId, agentId, cwd, AgentSessions.OnThisMachine(config).IsClaimed, HookCommand(), time);

    public static Task<bool> CoversAsync(ConfigRoot config, string sessionId, string? cwd, TimeProvider time) =>
        GitHookProbe.CoversAsync(sessionId, cwd, AgentSessions.OnThisMachine(config).IsClaimed, HookCommand(), time);

    static string HookCommand() => GitHookProbe.CommandFor(KcapBinaryCommand.Resolve());

    /// <summary>
    /// Always 0 and silent: the commit has already landed.
    /// </summary>
    public static async Task<int> RunAsync(GitHookInvocation invocation, ConfigRoot config, TimeProvider time) {
        try {
            await RecordAsync(invocation, AgentSessions.OnThisMachine(config), config, time);
        } catch { }

        return 0;
    }

    /// <param name="gitTimeout">Caps each git call; a test on a loaded runner passes a longer one.</param>
    internal static async Task RecordAsync(
            GitHookInvocation invocation, AgentSessions sessions, ConfigRoot config, TimeProvider time, TimeSpan? gitTimeout = null) {
        // The hook's clock, not git's: git's dates are whole seconds and the caller can set them.
        var committedAt = time.GetUtcNow();
        var timeout     = gitTimeout ?? GitTimeout;

        if (sessions.Above(invocation.Pid) is not { } session) return;

        var run = RepositoryDetection.DefaultRunner(time);
        var dir = invocation.Dir;

        // post-commit passes no arguments and post-merge its squash flag. A squash merge has committed nothing yet.
        var committed = invocation.Args switch {
            []    => true,
            ["0"] => await MergedNowAsync(run, dir, timeout),
            _     => false,
        };

        if (!committed || await run("git", "log -1 --format=%H%x1f%B", dir, timeout) is not { } log || log.Split('\x1f', 2) is not [var sha, var body])
            return;

        var repo    = await RepositoryDetection.DetectRepositoryAsync(new(), config, dir, time, detectPullRequest: false);
        var message = body.Trim();

        SessionCommits.Of(config, session).Append(new ObservedCommit {
            Sha         = sha,
            Owner       = repo?.Owner,
            RepoName    = repo?.RepoName,
            Branch      = repo?.Branch is { Length: > 0 } branch ? branch : null,
            CommittedAt = committedAt,
            Message     = SecretRedactor.RedactValue(message, keyIsSecret: false) ?? message,
        });
    }

    // A fast-forward moves HEAD onto a commit another ref already holds. A merge git just made is
    // held by the current branch alone.
    static async Task<bool> MergedNowAsync(CommandRunner run, string dir, TimeSpan timeout) {
        // %(HEAD) prints '*' before the branch HEAD is on.
        var holders = await run("git", "for-each-ref --contains HEAD --format=%(HEAD)%(refname)", dir, timeout);

        return holders is not null && holders
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .All(holder => holder.StartsWith('*'));
    }
}
