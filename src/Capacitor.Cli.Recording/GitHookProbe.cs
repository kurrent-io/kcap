using Capacitor.Cli.Core;
using Capacitor.Cli.Recording.PrDetection;

namespace Capacitor.Cli.Recording;

/// <summary>
/// Whether kcap's git hook files a session's commits. The host says what only it can know: which process's claim
/// names the session, and the command its install wrote, which a daemon can't read off its own binary.
/// </summary>
public static class GitHookProbe {
    public const string Name = "kcap";

    static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// The hook's command for <paramref name="binary"/>. git runs it through sh: single-quoted, with
    /// forward slashes for Git for Windows.
    /// </summary>
    public static string CommandFor(string binary) =>
        $"'{binary.Replace('\\', '/').Replace("'", "'\\''")}' git-hook";

    /// <summary>
    /// Whether <c>git hook list</c> output names kcap's hook, enabled.
    /// </summary>
    public static bool ListedIn(string? hookList) =>
        hookList?.Split('\n').Any(hook => hook.Trim() == Name) == true;

    /// <summary>
    /// The <see cref="CommitObservation"/> a recorder starts with.
    /// </summary>
    /// <param name="isClaimed">Whether a live agent process runs the session, so the git hook files its commits.</param>
    /// <param name="hookCommand">The command the hook entry has to name, from <see cref="CommandFor"/>.</param>
    public static async Task<CommitObservation> ObservationAsync(
            ConfigRoot config, string sessionId, string? agentId, string? cwd, Func<SessionId, bool> isClaimed, string hookCommand, TimeProvider time, CommandRunner? run = null) {
        if (!await CoversAsync(sessionId, cwd, isClaimed, hookCommand, time, run)) return new CommitObservation.Uncovered();

        return agentId is null ? new CommitObservation.Covered(SessionCommits.Of(config, SessionId.Parse(sessionId)!)) : new CommitObservation.Subagent();
    }

    /// <inheritdoc cref="ObservationAsync"/>
    public static async Task<bool> CoversAsync(string sessionId, string? cwd, Func<SessionId, bool> isClaimed, string hookCommand, TimeProvider time, CommandRunner? run = null) =>
        SessionId.Parse(sessionId) is { } session
     && isClaimed(session)
     && await RunsInAsync(cwd ?? "", hookCommand, run ?? RepositoryDetection.DefaultRunner(time));

    // Listed means git runs config hooks and the entry is enabled. The command must also be this
    // binary's: an entry left by a moved or removed kcap is listed, runs nothing, and would still
    // stop the server reading commits from the transcript.
    static async Task<bool> RunsInAsync(string dir, string hookCommand, CommandRunner run) =>
        ListedIn(await run("git", "hook list post-commit", dir, GitTimeout))
     && await run("git", $"config --get hook.{Name}.command", dir, GitTimeout) == hookCommand;
}
