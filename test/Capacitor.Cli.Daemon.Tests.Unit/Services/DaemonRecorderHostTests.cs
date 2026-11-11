using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Services;
using Capacitor.Cli.Recording;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>A hosted session's commits are the git hook's to file. Read wrongly, every hosted session would look
/// uncovered and the server would read commits from the transcript as well.</summary>
public class DaemonRecorderHostTests {
    const string Session = "0123456789abcdef0123456789abcdef";

    static readonly string HookCommand = GitHookProbe.CommandFor("/opt/kcap/kcap");

    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    DaemonRecorderHost Host(string? hookCommand) => new(
        Config.Root, hookCommand, TimeProvider.System, () => { }, (_, _, _) => { }, _ => { },
        (_, arguments, _, _) => Task.FromResult<string?>(arguments.StartsWith("hook list", StringComparison.Ordinal) ? GitHookProbe.Name : HookCommand));

    /// <summary>No hook has claimed this session in the claims file, and nothing here needs one to: the daemon started
    /// the process.</summary>
    [Test]
    public async Task A_hosted_session_is_claimed_by_definition() {
        var host = Host(HookCommand);

        await Assert.That(await host.ObserveCommitsAsync(Session, null, "/repo")).IsTypeOf<CommitObservation.Covered>();
        await Assert.That(await host.CoversCommitsAsync(Session, "/repo")).IsTrue();
    }

    /// <summary>The hook has to name the CLI, not this process: a daemon reading its own path would read every hosted
    /// session as uncovered.</summary>
    [Test]
    public async Task A_hook_naming_another_binary_covers_nothing() {
        var host = Host(GitHookProbe.CommandFor("/usr/local/bin/kcap-daemon"));

        await Assert.That(await host.ObserveCommitsAsync(Session, null, "/repo")).IsTypeOf<CommitObservation.Uncovered>();
    }

    [Test]
    public async Task No_CLI_beside_the_daemon_covers_nothing() {
        var host = Host(null);

        await Assert.That(await host.ObserveCommitsAsync(Session, null, "/repo")).IsTypeOf<CommitObservation.Uncovered>();
        await Assert.That(await host.CoversCommitsAsync(Session, "/repo")).IsFalse();
    }

    [Test]
    public async Task A_subagents_commits_are_its_parents() =>
        await Assert.That(await Host(HookCommand).ObserveCommitsAsync(Session, "a1", "/repo")).IsTypeOf<CommitObservation.Subagent>();

    /// <summary>A scan that took this for a recorder would treat its child as handled.</summary>
    [Test]
    public async Task A_scan_is_told_no_recorder_started() =>
        await Assert.That(await Host(HookCommand).StartChildAsync(new RecordingSource(Session, "a1", "/t.jsonl", "claude", null))).IsFalse();
}
