using System.Text.Json;
using Capacitor.Cli.Core.Harness.Gemini;
using Capacitor.Cli.Core.RepoEvidence;

namespace Capacitor.Cli.Core.Tests.Unit.Harness.Gemini;

public class GeminiCreatedPullRequestAdapterTests {
    const string Url = "https://github.com/kurrent-io/kcap/pull/1945";

    static string Json(string value) => JsonSerializer.Serialize(value);

    static string ShellTurn(string id, string command, string output, string status = "success") =>
        "{\"id\":\"g1\",\"timestamp\":\"2026-06-22T14:31:48.609Z\",\"type\":\"gemini\",\"content\":\"\",\"thoughts\":[],\"model\":\"gemini-3-flash-preview\",\"toolCalls\":[{\"id\":\"" + id
      + "\",\"name\":\"run_shell_command\",\"args\":{\"command\":" + Json(command) + ",\"description\":\"Open the PR.\"},\"result\":[{\"functionResponse\":{\"id\":\"" + id
      + "\",\"name\":\"run_shell_command\",\"response\":{\"output\":" + Json(output) + "}}}],\"status\":\"" + status
      + "\",\"timestamp\":\"2026-06-22T14:31:48.626Z\",\"resultDisplay\":\"\",\"description\":\"Open the PR.\",\"displayName\":\"Shell\",\"renderOutputAsMarkdown\":false}]}";

    static IReadOnlyList<FoundPullRequest> Observe(params string[] lines) {
        var evidence = new CreatedPullRequestEvidence();
        foreach (var line in lines) GeminiCreatedPullRequestAdapter.Instance.Observe(line, evidence);
        return evidence.Drain();
    }

    [Test]
    public async Task A_create_whose_output_ends_with_the_url_is_found() {
        var found = Observe(ShellTurn("run_shell_command__1", "git push -u origin HEAD && gh pr create --fill", "Output: " + Url + "\nProcess Group PGID: 4242"));

        await Assert.That(found).IsEquivalentTo([new FoundPullRequest(new("kurrent-io", "kcap", 1945, Url), "run_shell_command__1")]);
    }

    [Test]
    public async Task The_older_labelled_report_is_read_too() {
        const string report = "Command: gh pr create --fill\nDirectory: (root)\nStdout: " + Url + "\nStderr: (empty)\nError: (none)\nExit Code: 0\nSignal: (none)\nBackground PIDs: (none)\nProcess Group PGID: 4242";

        await Assert.That(Observe(ShellTurn("c1", "gh pr create --fill", report)).Select(f => f.Pr.Number)).IsEquivalentTo([1945]);
    }

    // gh's refusal exits 1, which the report states even though the tool call itself completed.
    [Test]
    public async Task A_create_gh_refused_because_the_pr_exists_is_not_found() {
        const string report = "Output: a pull request for branch \"feature\" into branch \"main\" already exists:\n" + Url + "\nExit Code: 1\nProcess Group PGID: 4242";

        await Assert.That(Observe(ShellTurn("c1", "gh pr create --fill", report))).IsEmpty();
        await Assert.That(Observe(ShellTurn("c2", "gh pr create --fill", "Output: " + Url, status: "error"))).IsEmpty();
        await Assert.That(Observe(ShellTurn("c3", "gh pr create --fill", "Output: " + Url + "\nSignal: 15\nProcess Group PGID: 4242"))).IsEmpty();
        await Assert.That(Observe(ShellTurn("c6", "gh pr create --fill", "Command: gh pr create --fill\nDirectory: (root)\nStdout: " + Url + "\nStderr: (empty)\nProcess Group PGID: 1"))).IsEmpty();
        await Assert.That(Observe(ShellTurn("c5", "gh pr create --fill", "Output: already exists:\n" + Url + "\n\nExit Code: 1\n\nProcess Group PGID: 42\n\n"))).IsEmpty();
        await Assert.That(Observe(ShellTurn("c4", "gh pr create --fill", "Command was cancelled by user before it could complete. Below is the output before it was cancelled:\n" + Url))).IsEmpty();
    }

    [Test]
    public async Task A_url_from_another_command_or_in_prose_is_not_found() {
        var found = Observe(
            ShellTurn("c1", "gh pr view --json url -q .url", "Output: " + Url + "\nProcess Group PGID: 1"),
            ShellTurn("c2", "echo 'gh pr create --fill'", "Output: " + Url + "\nProcess Group PGID: 1"),
            "{\"id\":\"g2\",\"type\":\"gemini\",\"content\":\"Opened " + Url + "\",\"model\":\"gemini-3-flash-preview\"}");

        await Assert.That(found).IsEmpty();
    }

    // Gemini writes a tool turn twice under one id, a snapshot and then the whole turn; one PR comes of it.
    [Test]
    public async Task A_turn_written_twice_yields_the_same_pr() {
        var turn = ShellTurn("c1", "gh pr create --fill", "Output: " + Url + "\nProcess Group PGID: 1");

        await Assert.That(Observe(turn, turn).Select(f => f.Pr).Distinct().Count()).IsEqualTo(1);
    }

    // A subagent writes its own chats/<parent>/<child>.jsonl, read by a child watcher with this same adapter.
    [Test]
    public async Task A_subagent_files_create_is_found_the_same_way() {
        var child = ShellTurn("run_shell_command__child", "gh pr create --fill", "Output: " + Url + "\nProcess Group PGID: 7");

        await Assert.That(Observe(child).Select(f => f.CallId ?? "")).IsEquivalentTo(["run_shell_command__child"]);
    }

    [Test]
    public async Task A_malformed_line_is_ignored() {
        await Assert.That(Observe("""{"type":"gemini","toolCalls":[{"name":"run_shell_command" """)).IsEmpty();
    }
}
