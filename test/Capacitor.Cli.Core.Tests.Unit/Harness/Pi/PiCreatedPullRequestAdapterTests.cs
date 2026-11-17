using System.Text.Json;
using Capacitor.Cli.Core.Harness.Pi;
using Capacitor.Cli.Core.RepoEvidence;

namespace Capacitor.Cli.Core.Tests.Unit.Harness.Pi;

public class PiCreatedPullRequestAdapterTests {
    const string Url = "https://github.com/kurrent-io/kcap/pull/1945";

    static string ToolCall(string id, string command) =>
        $$$"""{"type":"message","id":"a1","parentId":"u0","timestamp":"2026-06-18T12:00:06.000Z","message":{"role":"assistant","content":[{"type":"text","text":"Opening the PR."},{"type":"toolCall","id":"{{{id}}}","name":"bash","arguments":{"command":{{{JsonSerializer.Serialize(command)}}}}}],"stopReason":"toolUse"}}""";

    static string ToolResult(string id, string text, bool isError = false) =>
        $$$"""{"type":"message","id":"r1","parentId":"a1","timestamp":"2026-06-18T12:00:07.000Z","message":{"role":"toolResult","toolCallId":"{{{id}}}","toolName":"bash","content":[{"type":"text","text":{{{JsonSerializer.Serialize(text)}}}}],"isError":{{{(isError ? "true" : "false")}}}}}""";

    static string UserBash(string command, string output, int exitCode) =>
        $$$"""{"type":"message","id":"b1","parentId":"r1","timestamp":"2026-06-18T12:00:08.000Z","message":{"role":"bashExecution","command":{{{JsonSerializer.Serialize(command)}}},"output":{{{JsonSerializer.Serialize(output)}}},"exitCode":{{{exitCode}}},"cancelled":false,"truncated":false}}""";

    static IReadOnlyList<FoundPullRequest> Observe(params string[] lines) {
        var evidence = new CreatedPullRequestEvidence();
        foreach (var line in lines) PiCreatedPullRequestAdapter.Instance.Observe(line, evidence);
        return evidence.Drain();
    }

    [Test]
    public async Task A_bash_create_paired_with_its_result_is_found() {
        var found = Observe(ToolCall("call_pr_1", "git push -u origin HEAD && gh pr create --fill"), ToolResult("call_pr_1", "Creating pull request\n\n" + Url + "\n"));

        await Assert.That(found).IsEquivalentTo([new FoundPullRequest(new("kurrent-io", "kcap", 1945, Url), "call_pr_1")]);
    }

    [Test]
    public async Task A_create_gh_refused_because_the_pr_exists_is_not_found() {
        var found = Observe(ToolCall("call_pr_1", "gh pr create --fill"),
            ToolResult("call_pr_1", "a pull request for branch \"feature\" into branch \"main\" already exists:\n" + Url + "\n\nCommand exited with code 1", isError: true));

        await Assert.That(found).IsEmpty();
    }

    [Test]
    public async Task A_url_in_another_result_or_in_prose_is_not_found() {
        var found = Observe(
            ToolCall("call_view", "gh pr view --json url -q .url"), ToolResult("call_view", Url),
            ToolCall("call_echo", "echo 'next: gh pr create --fill'"), ToolResult("call_echo", Url),
            """{"type":"message","id":"a2","message":{"role":"assistant","content":[{"type":"text","text":"Opened https://github.com/kurrent-io/kcap/pull/1945"}]}}""");

        await Assert.That(found).IsEmpty();
    }

    [Test]
    public async Task A_result_for_another_call_id_is_not_found() {
        var found = Observe(ToolCall("call_pr_1", "gh pr create --fill"), ToolResult("call_other", Url));

        await Assert.That(found).IsEmpty();
    }

    [Test]
    public async Task The_users_own_shell_create_is_found_only_when_it_exited_zero() {
        await Assert.That(Observe(UserBash("gh pr create --fill", Url + "\n", 0)).Select(f => f.Pr.Number)).IsEquivalentTo([1945]);
        await Assert.That(Observe(UserBash("gh pr create --fill", "already exists:\n" + Url + "\n", 1))).IsEmpty();
    }

    // The child's own call and result, carried inside the subagent tool's result on the parent's line.
    [Test]
    public async Task A_create_inside_a_subagent_result_is_found() {
        var subagentResult = $$$"""
            {"type":"message","id":"r2","parentId":"a2","timestamp":"2026-06-23T16:45:15.000Z","message":{"role":"toolResult","toolCallId":"toolu_subagent","toolName":"subagent","content":[{"type":"text","text":"Opened the PR."}],"details":{"mode":"single","results":[{"agent":"shipper","exitCode":0,"messages":[
              {"role":"user","content":[{"type":"text","text":"Task: open the PR"}]},
              {"role":"assistant","content":[{"type":"toolCall","id":"toolu_child_pr","name":"bash","arguments":{"command":"gh pr create --fill"}}],"stopReason":"toolUse"},
              {"role":"toolResult","toolCallId":"toolu_child_pr","toolName":"bash","content":[{"type":"text","text":"{{{Url}}}"}],"isError":false},
              {"role":"assistant","content":[{"type":"text","text":"Opened the PR."}],"stopReason":"stop"}]}]},"isError":false}}
            """.ReplaceLineEndings("");

        var found = Observe(subagentResult);

        await Assert.That(found.Select(f => f.CallId ?? "")).IsEquivalentTo(["toolu_child_pr"]);
    }

    [Test]
    public async Task A_malformed_line_is_ignored() {
        await Assert.That(Observe("""{"type":"message","message":{"role":"toolCall" """)).IsEmpty();
    }
}
