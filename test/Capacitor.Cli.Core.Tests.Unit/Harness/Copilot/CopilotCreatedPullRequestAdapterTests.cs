using System.Text.Json;
using Capacitor.Cli.Core.Harness.Copilot;
using Capacitor.Cli.Core.RepoEvidence;

namespace Capacitor.Cli.Core.Tests.Unit.Harness.Copilot;

public class CopilotCreatedPullRequestAdapterTests {
    const string Url = "https://github.com/kurrent-io/kcap/pull/1945";

    static string Json(string value) => JsonSerializer.Serialize(value);

    static string Request(string callId, string name, string argumentsJson, string? agentId = null) =>
        "{\"type\":\"assistant.message\",\"data\":{\"messageId\":\"m1\",\"model\":\"claude-sonnet-5.5\",\"content\":\"Opening the PR.\",\"toolRequests\":[{\"toolCallId\":\""
      + callId + "\",\"name\":\"" + name + "\",\"arguments\":" + argumentsJson + ",\"type\":\"function\"}]},\"id\":\"e1\",\"timestamp\":\"2026-11-15T09:00:04.000Z\",\"parentId\":null"
      + (agentId is null ? "" : ",\"agentId\":\"" + agentId + "\"") + "}";

    static string Bash(string callId, string command, string? agentId = null) =>
        Request(callId, "bash", "{\"command\":" + Json(command) + ",\"description\":\"Open the PR\"}", agentId);

    static string Complete(string callId, string content, bool success = true) =>
        "{\"type\":\"tool.execution_complete\",\"data\":{\"toolCallId\":\"" + callId + "\",\"success\":" + (success ? "true" : "false")
      + ",\"result\":{\"content\":" + Json(content) + ",\"detailedContent\":" + Json(content) + "}},\"id\":\"e2\",\"timestamp\":\"2026-11-15T09:00:05.000Z\",\"parentId\":\"e1\"}";

    static IReadOnlyList<FoundPullRequest> Observe(params string[] lines) {
        var evidence = new CreatedPullRequestEvidence();
        foreach (var line in lines) CopilotCreatedPullRequestAdapter.Instance.Observe(line, evidence);
        return evidence.Drain();
    }

    [Test]
    public async Task A_bash_create_paired_with_its_completion_is_found() {
        var found = Observe(Bash("toolu_pr", "git push -u origin HEAD && gh pr create --fill"), Complete("toolu_pr", Url + "\n<shellId: 4 completed with exit code 0>"));

        await Assert.That(found).IsEquivalentTo([new FoundPullRequest(new("kurrent-io", "kcap", 1945, Url), "toolu_pr")]);
    }

    [Test]
    public async Task Arguments_given_as_a_json_string_are_read() {
        var found = Observe(Request("toolu_pr", "bash", Json("{\"command\":\"gh pr create --fill\"}")), Complete("toolu_pr", Url + "\n<shellId: 4 completed with exit code 0>"));

        await Assert.That(found.Count).IsEqualTo(1);
    }

    // The call reports success; the shell's own trailer says gh exited 1.
    [Test]
    public async Task A_create_gh_refused_because_the_pr_exists_is_not_found() {
        var found = Observe(Bash("toolu_pr", "gh pr create --fill"),
            Complete("toolu_pr", "a pull request for branch \"feature\" into branch \"main\" already exists:\n" + Url + "\n<shellId: 4 completed with exit code 1>"));

        await Assert.That(found).IsEmpty();
    }

    [Test]
    public async Task A_result_without_the_shell_trailer_is_not_found() {
        await Assert.That(Observe(Bash("toolu_pr", "gh pr create --fill"), Complete("toolu_pr", "already exists:\n" + Url))).IsEmpty();
    }

    [Test]
    public async Task A_url_from_another_command_or_in_prose_is_not_found() {
        var found = Observe(
            Bash("toolu_view", "gh pr view --json url -q .url"), Complete("toolu_view", Url + "\n<shellId: 1 completed with exit code 0>"),
            Bash("toolu_echo", "echo 'gh pr create --fill'"), Complete("toolu_echo", Url + "\n<shellId: 2 completed with exit code 0>"),
            "{\"type\":\"assistant.message\",\"data\":{\"messageId\":\"m2\",\"content\":\"Opened " + Url + " for review\",\"toolRequests\":[]}}");

        await Assert.That(found).IsEmpty();
    }

    // The create outlives the call's wait; its URL and exit code come back through read_bash on the same shell.
    [Test]
    public async Task A_create_still_running_is_found_from_its_read_bash() {
        var found = Observe(
            Bash("toolu_pr", "gh pr create --fill"),
            Complete("toolu_pr", "<command with shellId: 3 is still running after 30 seconds. The command is still running but hasn't produced output yet. Use read_bash to continue waiting, or stop_bash to stop it.>"),
            Request("toolu_read", "read_bash", "{\"shellId\":\"3\",\"delay\":30}"),
            Complete("toolu_read", Url + "\n<shellId: 3 completed with exit code 0>"));

        await Assert.That(found.Select(f => f.CallId ?? "")).IsEquivalentTo(["copilot-shell:3"]);
    }

    // Output written before the wait lapsed comes ahead of the still-running marker.
    [Test]
    public async Task A_still_running_create_with_partial_output_waits_for_its_read_bash() {
        var found = Observe(
            Bash("toolu_pr", "gh pr create --fill"),
            Complete("toolu_pr", "Creating pull request for feature into main in kurrent-io/kcap\n\n<command with shellId: 3 is still running after 30 seconds. The command is still running. Use read_bash to continue waiting, or stop_bash to stop it.>"),
            Complete("toolu_read", Url + "\n<shellId: 3 completed with exit code 0>"));

        await Assert.That(found.Select(f => f.CallId ?? "")).IsEquivalentTo(["copilot-shell:3"]);
    }

    [Test]
    public async Task A_read_bash_of_another_shell_is_not_tied_to_the_create() {
        var found = Observe(
            Bash("toolu_pr", "gh pr create --fill"),
            Complete("toolu_pr", "<command with shellId: 3 is still running after 30 seconds. The command is still running but hasn't produced output yet. Use read_bash to continue waiting, or stop_bash to stop it.>"),
            Complete("toolu_read", Url + "\n<shellId: 5 completed with exit code 0>"));

        await Assert.That(found).IsEmpty();
    }

    // A subagent's records sit in the session's own events.jsonl, tagged with its agentId.
    [Test]
    public async Task A_subagents_create_in_the_session_file_is_found() {
        var found = Observe(Bash("toolu_child", "gh pr create --fill", agentId: "agent-7"), Complete("toolu_child", Url + "\n<shellId: 9 completed with exit code 0>"));

        await Assert.That(found.Count).IsEqualTo(1);
    }

    [Test]
    public async Task A_malformed_line_is_ignored() {
        await Assert.That(Observe("{\"type\":\"assistant.message\",\"data\":{\"toolRequests\":[{\"name\":\"bash\" create")).IsEmpty();
    }
}
