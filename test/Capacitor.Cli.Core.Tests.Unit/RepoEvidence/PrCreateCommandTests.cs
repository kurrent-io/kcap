using Capacitor.Cli.Core.RepoEvidence;

namespace Capacitor.Cli.Core.Tests.Unit.RepoEvidence;

public class PrCreateCommandTests {
    [Test]
    [Arguments("gh pr create --fill")]
    [Arguments("cd /work/app && gh pr create --title t --body b")]
    [Arguments("git push -u origin HEAD; gh pr create --fill 2>&1")]
    [Arguments("gh pr create --fill 2>/tmp/gh.err")]
    [Arguments("gh pr create --fill 2>>errors.log")]
    [Arguments("gh pr create --fill 2> /dev/null")]
    [Arguments("(cd app && gh pr create --fill) 2>&1")]
    [Arguments("bash -c 'gh pr create --title \"$1\" --fill' _ '>new feature'")]
    [Arguments("gh pr create --fill --title '>quoted'")]
    [Arguments("git push || true; gh pr create --fill")]
    [Arguments("git push || (git status); gh pr create --fill")]
    [Arguments("(git push || true) && gh pr create --fill")]
    [Arguments("url=$(gh pr create --fill) &&\necho \"$url\"")]
    [Arguments("GH_TOKEN=x /opt/homebrew/bin/gh pr create --fill")]
    [Arguments("bash -lc 'cd app && gh pr create --fill'")]
    [Arguments("url=$(gh pr create --fill) && echo \"$url\"")]
    [Arguments("url=$(gh pr create --fill) && echo ${url}")]
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
    [Arguments("printf '%s\\n' https://github.com/o/r/pull/1; gh pr create --repo o/r --fill >/dev/null 2>&1")]
    [Arguments("gh pr create --fill > out.txt")]
    [Arguments("url=$(gh pr create --repo o/r --fill) && echo \"$url\" >/dev/null")]
    [Arguments("(gh pr create --repo o/r --fill) >/dev/null")]
    [Arguments("! gh pr create --fill 2>&1")]
    [Arguments("bash -c '! gh pr create --fill 2>&1'")]
    [Arguments("gh pr create --repo o/r --title \"2\">/dev/null --body b")]
    [Arguments("gh pr create --repo o/r --title \\1>/dev/null --body b")]
    [Arguments("gh pr create --repo o/r --title=New>/dev/null --body=b")]
    [Arguments("gh pr create --repo o/r --title >/dev/null 'New PR' --body b")]
    [Arguments(">/dev/null gh pr create --fill")]
    [Arguments("printf '%s\\n' https://github.com/o/r/pull/1; bash -c 'gh pr create --repo o/r --fill' >/dev/null")]
    [Arguments("bash -c 'gh pr create --fill' 1>out.txt")]
    [Arguments("gh pr create --fill 1>>out.txt")]
    [Arguments("echo https://github.com/o/r/pull/1; url=$(gh pr create --fill)")]
    [Arguments("gh pr create --fill || gh pr view --json url --jq .url")]
    [Arguments("gh pr view --json url --jq .url || gh pr create --fill")]
    [Arguments("gh pr view --json url --jq .url ||\ngh pr create --fill")]
    [Arguments("gh pr view --json url --jq .url || (cd repo; gh pr create --fill)")]
    [Arguments("gh pr view --json url --jq .url || { cd repo; gh pr create --fill; }")]
    [Arguments("gh pr view --json url --jq .url || bash -c 'gh pr create --fill'")]
    [Arguments("gh pr view --json url --jq .url || url=$(gh pr create --fill) && echo \"$url\"")]
    [Arguments("gh pr create --fill; gh pr view --json url --jq .url")]
    [Arguments("url=$(gh pr create --fill || gh pr view --json url --jq .url) && echo \"$url\"")]
    [Arguments("url=$(gh pr create --fill) || gh pr view --json url --jq .url")]
    [Arguments("url=$(gh pr create --fill) || echo \"$(gh pr view --json url --jq .url)\"")]
    [Arguments("url=$(gh pr create --fill); echo \"$(gh pr view --json url --jq .url)\"")]
    [Arguments("url=$(gh pr create --fill); echo \"$existing\"")]
    [Arguments("url=$(gh pr create --fill) && echo \"$existing\"")]
    [Arguments("echo \"$(gh pr create --fill)\"")]
    [Arguments("url=$(gh pr create --fill) && echo \"$url\" && echo \"$existing\"")]
    [Arguments("url=$(gh pr create --fill)\necho \"$existing\"")]
    [Arguments("echo \"$(gh pr create --fill)\" \"$(gh pr view --json url --jq .url)\"")]
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
    [Arguments("gh pr create --repo 2>/dev/null o/r --fill", "o/r")]
    [Arguments("gh pr create --repo 2> /dev/null o/r --fill", "o/r")]
    public async Task The_repo_flag_is_returned(string command, string? expected) {
        await Assert.That(PrCreateCommand.TryMatch(command, out var repo)).IsTrue();
        await Assert.That(repo).IsEqualTo(expected);
    }

    [Test]
    [Arguments("cd /work/app && gh pr create --fill 2>&1 | tail -5", true)]
    [Arguments("cd /work && cd app; gh pr create --fill 2>&1 | tail -5", true)]
    [Arguments("gh pr create --fill 2>&1 | tail -n 3", true)]
    [Arguments("gh pr create --fill | head -20", true)]
    [Arguments("gh pr create --fill | tail", true)]
    [Arguments("gh pr create --fill | tail --lines=2", true)]
    [Arguments("gh pr create --fill | tail -n5", true)]
    [Arguments("gh pr create --fill", false)]
    public async Task A_create_piped_through_tail_or_head_is_filtered(string command, bool filtered) {
        await Assert.That(PrCreateCommand.TryMatch(command, out _, out var isFiltered)).IsTrue();
        await Assert.That(isFiltered).IsEqualTo(filtered);
    }

    [Test]
    [Arguments("gh pr create --fill | tail -1 notes.txt")]
    [Arguments("gh pr create --fill | grep http")]
    [Arguments("gh pr create --fill 2>&1 | tail -1")]
    [Arguments("gh pr create --fill 2>&1 | tail -n 1")]
    [Arguments("gh pr create --fill | tail -c 60")]
    [Arguments("gh pr create --fill | tail -n +2")]
    [Arguments("gh pr create --fill | tail -2 < /tmp/old-pr-url")]
    [Arguments("printf '%s\\n' https://github.com/o/r/pull/1; gh pr create --fill 2>&1 | tail -2")]
    [Arguments("gh pr create --fill 2>/dev/null | tail -2")]
    [Arguments("echo https://github.com/o/r/pull/1; url=$(gh pr create --fill 2>&1 | tail -2) && echo \"$url\"")]
    [Arguments("gh pr create --fill | tail -2 <<EOF\nhttps://github.com/o/r/pull/1\nEOF")]
    [Arguments("gh pr create --fill | tail -1 | cat")]
    [Arguments("gh pr create --fill | tail -1 > out.txt")]
    public async Task Any_other_pipe_after_a_create_does_not_match(string command) {
        await Assert.That(PrCreateCommand.TryMatch(command, out _)).IsFalse();
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
