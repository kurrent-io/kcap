using System.Text.Json;
using Capacitor.Cli.Core.Harness.Grok;
using Capacitor.Cli.Core.RepoEvidence;

namespace Capacitor.Cli.Core.Tests.Unit.Harness.Grok;

public class GrokCreatedPullRequestAdapterTests {
    const string Url = "https://github.com/kurrent-io/kcap/pull/1945";

    static string Json(string value) => JsonSerializer.Serialize(value);

    static string Update(string callId, string status, string content, string rawOutput) =>
        "{\"timestamp\":1791900944,\"method\":\"session/update\",\"params\":{\"sessionId\":\"s1\",\"update\":{\"sessionUpdate\":\"tool_call_update\",\"toolCallId\":\""
      + callId + "\",\"status\":\"" + status + "\",\"content\":" + content + ",\"rawOutput\":" + rawOutput + "},\"_meta\":{\"eventId\":\"s1-5\",\"promptId\":\"p1\"}}}";

    static string Terminal(string callId, string command, string output, int exitCode, string status = "completed") =>
        Update(callId, status, "[{\"type\":\"content\",\"content\":{\"type\":\"text\",\"text\":" + Json(output) + "}}]",
            "{\"type\":\"Bash\",\"output\":[104,105],\"output_for_prompt\":" + Json(output) + ",\"exit_code\":" + exitCode + ",\"command\":" + Json(command)
          + ",\"truncated\":false,\"signal\":null,\"timed_out\":false,\"description\":\"Open the PR\",\"current_dir\":\"/work/app\",\"output_file\":\"/tmp/out\",\"total_bytes\":2}");

    static string BackgroundOutput(string callId, string command, string output, int exitCode) =>
        Update(callId, "completed", "[]",
            "{\"type\":\"TaskOutput\",\"Result\":{\"task_id\":\"t1\",\"command\":" + Json(command) + ",\"status\":\"completed\",\"exit_code\":" + exitCode
          + ",\"started\":\"2026-11-10T09:00:00Z\",\"ended\":\"2026-11-10T09:00:04Z\",\"duration_secs\":4.0,\"output\":" + Json(output)
          + ",\"output_file\":\"\",\"truncated\":false,\"truncation_hint\":\"\",\"raw_output_bytes\":60}}");

    static IReadOnlyList<FoundPullRequest> Observe(params string[] lines) {
        var evidence = new CreatedPullRequestEvidence();
        foreach (var line in lines) GrokCreatedPullRequestAdapter.Instance.Observe(line, evidence);
        return evidence.Drain();
    }

    [Test]
    public async Task A_terminal_create_is_found() {
        var found = Observe(Terminal("call_pr", "git push -u origin HEAD && gh pr create --fill", Url + "\n", 0));

        await Assert.That(found).IsEquivalentTo([new FoundPullRequest(new("kurrent-io", "kcap", 1945, Url), "call_pr")]);
    }

    // A create run in the background reports its command, exit code and output when read back.
    [Test]
    public async Task A_backgrounded_create_is_found_from_its_output_read() {
        var found = Observe(BackgroundOutput("call_read", "gh pr create --fill", Url + "\n", 0));

        await Assert.That(found.Select(f => f.CallId ?? "")).IsEquivalentTo(["call_read"]);
    }

    [Test]
    public async Task A_create_gh_refused_because_the_pr_exists_is_not_found() {
        const string output = "a pull request for branch \"feature\" into branch \"main\" already exists:\n" + Url + "\n";

        await Assert.That(Observe(Terminal("c1", "gh pr create --fill", output, 1, status: "failed"))).IsEmpty();
        await Assert.That(Observe(Terminal("c2", "gh pr create --fill", output, 1))).IsEmpty();
        await Assert.That(Observe(BackgroundOutput("c3", "gh pr create --fill", output, 1))).IsEmpty();
    }

    [Test]
    public async Task A_url_from_another_command_or_in_prose_is_not_found() {
        var found = Observe(
            Terminal("c1", "gh pr view --json url -q .url", Url, 0),
            Terminal("c2", "echo 'gh pr create --fill'", Url, 0),
            "{\"method\":\"session/update\",\"params\":{\"update\":{\"sessionUpdate\":\"agent_message_chunk\",\"content\":{\"type\":\"text\",\"text\":\"Opened " + Url + "\"}}}}");

        await Assert.That(found).IsEmpty();
    }

    // Truncated output may have dropped the line that really came last.
    [Test]
    public async Task A_truncated_output_is_not_read() {
        var truncated = Terminal("c1", "gh pr create --fill", Url, 0).Replace("\"truncated\":false", "\"truncated\":true");

        await Assert.That(Observe(truncated)).IsEmpty();
    }

    // A subagent is a sibling session whose own updates.jsonl a child watcher reads with this same adapter.
    [Test]
    public async Task A_subagent_sessions_create_is_found_the_same_way() {
        await Assert.That(Observe(Terminal("child_call", "gh pr create --fill", Url, 0)).Count).IsEqualTo(1);
    }

    [Test]
    public async Task A_malformed_line_is_ignored() {
        await Assert.That(Observe("{\"params\":{\"update\":{\"sessionUpdate\":\"tool_call_update\",\"exit_code\"")).IsEmpty();
    }
}
