using Capacitor.Cli.Core;
using Capacitor.Cli.Recording.PrDetection;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Recording.Tests.Unit;

public class GitHookProbeTests {
    const string Session = "s1";
    const string Command = "'/opt/kcap' git-hook";

    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    readonly FakeTimeProvider _time = new();

    static CommandRunner Git(string? hookList, string? command) => (_, arguments, _, _) =>
        Task.FromResult(arguments.StartsWith("hook list", StringComparison.Ordinal) ? hookList : command);

    Task<bool> Covers(bool claimed, CommandRunner run, string sessionId = Session) =>
        GitHookProbe.CoversAsync(sessionId, "/repo", _ => claimed, Command, _time, run);

    [Test]
    [Arguments("kcap", true)]
    [Arguments("husky\nkcap\n", true)]
    [Arguments("disabled\tkcap", false)]
    [Arguments(null, false)]
    public async Task Only_an_enabled_kcap_entry_counts_as_listed(string? hookList, bool listed) =>
        await Assert.That(GitHookProbe.ListedIn(hookList)).IsEqualTo(listed);

    [Test]
    public async Task A_claimed_session_with_this_binarys_hook_is_covered() =>
        await Assert.That(await Covers(claimed: true, Git("kcap", Command))).IsTrue();

    [Test]
    public async Task A_session_no_live_agent_claims_is_not_covered_and_runs_no_git() {
        var ran = false;

        await Assert.That(await Covers(claimed: false, (_, _, _, _) => {
            ran = true;

            return Task.FromResult<string?>("kcap");
        })).IsFalse();

        await Assert.That(ran).IsFalse();
    }

    /// <summary>An entry left by a moved or removed kcap is listed and runs nothing.</summary>
    [Test]
    public async Task A_hook_entry_naming_another_binary_does_not_cover() =>
        await Assert.That(await Covers(claimed: true, Git("kcap", "'/gone/kcap' git-hook"))).IsFalse();

    [Test]
    public async Task A_hook_git_does_not_list_does_not_cover() =>
        await Assert.That(await Covers(claimed: true, Git("husky", Command))).IsFalse();

    [Test]
    public async Task A_session_is_observed_as_covered_and_a_subagent_as_its_parents() {
        await Assert.That(await GitHookProbe.ObservationAsync(Config.Root, Session, null, "/repo", _ => true, Command, _time, Git("kcap", Command)))
            .IsTypeOf<CommitObservation.Covered>();
        await Assert.That(await GitHookProbe.ObservationAsync(Config.Root, Session, "a1", "/repo", _ => true, Command, _time, Git("kcap", Command)))
            .IsTypeOf<CommitObservation.Subagent>();
    }

    [Test]
    public async Task An_uncovered_session_is_observed_as_uncovered() =>
        await Assert.That(await GitHookProbe.ObservationAsync(Config.Root, Session, null, "/repo", _ => false, Command, _time, Git("kcap", Command)))
            .IsTypeOf<CommitObservation.Uncovered>();

    [Test]
    public async Task The_hook_command_quotes_the_binary_for_sh_with_forward_slashes() =>
        await Assert.That(GitHookProbe.CommandFor(@"C:\it's\kcap.exe")).IsEqualTo("""'C:/it'\''s/kcap.exe' git-hook""");
}
