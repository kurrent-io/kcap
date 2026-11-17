using System.Text.Json;
using Capacitor.Cli.Core.Harness.OpenCode;
using Capacitor.Cli.Core.RepoEvidence;

namespace Capacitor.Cli.Core.Tests.Unit.Harness.OpenCode;

public class OpenCodeCreatedPullRequestAdapterTests {
    const string Url = "https://github.com/kurrent-io/kcap/pull/1945";

    static string Json(string value) => JsonSerializer.Serialize(value);

    static string Message(string state, string callId = "call_pr", string sessionId = "ses_oc_parent") =>
        "{\"info\":{\"role\":\"assistant\",\"id\":\"msg_a\",\"sessionID\":\"" + sessionId + "\"},\"parts\":[{\"type\":\"tool\",\"tool\":\"bash\",\"callID\":\"" + callId
      + "\",\"state\":" + state + ",\"id\":\"prt_y\",\"sessionID\":\"" + sessionId + "\",\"messageID\":\"msg_a\"}]}";

    static string Settled(string command, string output, int exit, string status = "completed") =>
        "{\"status\":\"" + status + "\",\"input\":{\"command\":" + Json(command) + ",\"description\":\"Open the PR\"},\"output\":" + Json(output)
      + ",\"metadata\":{\"output\":" + Json(output) + ",\"exit\":" + exit + ",\"description\":\"Open the PR\",\"truncated\":false},\"title\":\"Open the PR\",\"time\":{\"start\":1782241515157,\"end\":1782241516381}}";

    static IReadOnlyList<FoundPullRequest> Observe(params string[] lines) {
        var evidence = new CreatedPullRequestEvidence();
        foreach (var line in lines) OpenCodeCreatedPullRequestAdapter.Instance.Observe(line, evidence);
        return evidence.Drain();
    }

    [Test]
    public async Task A_settled_create_is_found() {
        var found = Observe(Message(Settled("git push -u origin HEAD && gh pr create --fill", Url + "\n", 0)));

        await Assert.That(found).IsEquivalentTo([new FoundPullRequest(new("kurrent-io", "kcap", 1945, Url), "call_pr")]);
    }

    // A running snapshot has no result yet; only the settled part counts.
    [Test]
    public async Task A_pending_or_running_snapshot_is_not_read() {
        var running = "{\"status\":\"running\",\"input\":{\"command\":\"gh pr create --fill\"},\"output\":" + Json(Url) + ",\"time\":{\"start\":1}}";

        await Assert.That(Observe(Message("{\"status\":\"pending\",\"input\":{\"command\":\"gh pr create --fill\"}}"), Message(running))).IsEmpty();
    }

    // OpenCode settles a non-zero exit as completed; the metadata's exit code says gh refused.
    [Test]
    public async Task A_create_gh_refused_because_the_pr_exists_is_not_found() {
        var output = "a pull request for branch \"feature\" into branch \"main\" already exists:\n" + Url + "\n";

        await Assert.That(Observe(Message(Settled("gh pr create --fill 2>&1", output, 1)))).IsEmpty();
        await Assert.That(Observe(Message(Settled("gh pr create --fill 2>&1", output, 1, status: "error")))).IsEmpty();
    }

    [Test]
    public async Task A_url_from_another_command_or_in_prose_is_not_found() {
        var found = Observe(
            Message(Settled("gh pr view --json url -q .url", Url, 0), callId: "c1"),
            Message(Settled("echo 'gh pr create --fill'", Url, 0), callId: "c2"),
            "{\"info\":{\"role\":\"assistant\",\"id\":\"msg_b\"},\"parts\":[{\"type\":\"text\",\"text\":\"Opened " + Url + " with bash\"}]}");

        await Assert.That(found).IsEmpty();
    }

    // Truncated output may have dropped the line that really came last.
    [Test]
    public async Task A_truncated_output_is_not_read() {
        var truncated = Settled("gh pr create --fill", Url, 0).Replace("\"truncated\":false", "\"truncated\":true");

        await Assert.That(Observe(Message(truncated))).IsEmpty();
    }

    // A subagent is a child session (parent_id set) whose own messages a child watcher reads with this same adapter.
    [Test]
    public async Task A_child_sessions_create_is_found_the_same_way() {
        var found = Observe(Message(Settled("gh pr create --fill", Url, 0), callId: "call_child", sessionId: "ses_oc_child"));

        await Assert.That(found.Select(f => f.CallId ?? "")).IsEquivalentTo(["call_child"]);
    }

    [Test]
    public async Task A_malformed_line_is_ignored() {
        await Assert.That(Observe("{\"parts\":[{\"type\":\"tool\",\"tool\":\"bash\"")).IsEmpty();
    }
}
