using Capacitor.Cli.Core.RepoEvidence;

namespace Capacitor.Cli.Core.Tests.Unit.RepoEvidence;

public class PrCreateCommandTests {
    [Test]
    [Arguments("gh pr create --fill")]
    [Arguments("cd /work/app && gh pr create --title t --body b")]
    [Arguments("git push -u origin HEAD; gh pr create --fill 2>&1")]
    [Arguments("GH_TOKEN=x /opt/homebrew/bin/gh pr create --fill")]
    [Arguments("bash -lc 'cd app && gh pr create --fill'")]
    [Arguments("url=$(gh pr create --fill) && echo \"$url\"")]
    [Arguments("echo '<<EOF'\ngh pr create --fill")]
    [Arguments("# <<EOF\ngh pr create --fill")]
    [Arguments("gh pr create --title '--dry-run' --fill")]
    public async Task A_run_create_matches(string command) {
        await Assert.That(PrCreateCommand.TryMatch(command, out _)).IsTrue();
    }

    [Test]
    [Arguments("echo 'gh pr create --fill'")]
    [Arguments("echo \"run gh pr create next\"")]
    [Arguments("gh pr view 12")]
    [Arguments("gh pr list --search create")]
    [Arguments("# gh pr create --fill")]
    [Arguments("grep -r 'gh pr create' docs")]
    [Arguments("gh pr create --fill --dry-run")]
    [Arguments("gh pr create --fill || gh pr view --json url --jq .url")]
    [Arguments("gh pr create --fill; gh pr view --json url --jq .url")]
    [Arguments("gh pr create --fill 2>&1 | tail -1")]
    [Arguments("url=$(gh pr create --fill || gh pr view --json url --jq .url) && echo \"$url\"")]
    [Arguments("url=$(gh pr create --fill) || gh pr view --json url --jq .url")]
    public async Task A_mention_or_another_command_does_not_match(string command) {
        await Assert.That(PrCreateCommand.TryMatch(command, out _)).IsFalse();
    }

    // A PR body routinely carries an apostrophe and its own mention of the command; neither may confuse the split.
    [Test]
    public async Task A_heredoc_body_is_data_not_commands() {
        const string create = """
            gh pr create --title "Fix it" --body "$(cat <<'EOF'
            It doesn't break "quotes"; it runs gh pr create --repo other/repo
            EOF
            )" --repo kurrent-io/kcap
            """;
        const string mention = """
            cat <<'EOF' > notes.md
            gh pr create --fill
            EOF
            """;

        await Assert.That(PrCreateCommand.TryMatch(create, out var repo)).IsTrue();
        await Assert.That(repo).IsEqualTo("kurrent-io/kcap");
        await Assert.That(PrCreateCommand.TryMatch(mention, out _)).IsFalse();
    }

    [Test]
    [Arguments("gh pr create --repo kurrent-io/kcap --fill", "kurrent-io/kcap")]
    [Arguments("gh pr create --repo=kurrent-io/kcap --fill", "kurrent-io/kcap")]
    [Arguments("gh pr create -R kurrent-io/kcap --fill", "kurrent-io/kcap")]
    [Arguments("gh pr create --fill", null)]
    [Arguments("gh pr create --body '--repo other/repo' --fill", null)]
    public async Task The_repo_flag_is_returned(string command, string? expected) {
        await Assert.That(PrCreateCommand.TryMatch(command, out var repo)).IsTrue();
        await Assert.That(repo).IsEqualTo(expected);
    }

    [Test]
    [Arguments("kurrent-io/kcap", true)]
    [Arguments("github.com/Kurrent-IO/KCAP", true)]
    [Arguments("https://github.com/kurrent-io/kcap.git", true)]
    [Arguments("kurrent-io/other", false)]
    [Arguments("kcap", false)]
    public async Task A_repo_value_matches_by_owner_and_name(string repo, bool matches) {
        await Assert.That(PrCreateCommand.RepoMatches(repo, "kurrent-io", "kcap")).IsEqualTo(matches);
    }
}
