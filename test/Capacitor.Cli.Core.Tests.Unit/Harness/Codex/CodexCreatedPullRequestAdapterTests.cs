using System.Text.Json;
using Capacitor.Cli.Core.Harness.Codex;
using Capacitor.Cli.Core.RepoEvidence;

namespace Capacitor.Cli.Core.Tests.Unit.Harness.Codex;

public class CodexCreatedPullRequestAdapterTests {
    const string Url = "https://github.com/kurrent-io/kcap/pull/1945";

    static string Json(string value) => JsonSerializer.Serialize(value);

    static string FunctionCall(string callId, string cmd) =>
        $$$"""{"timestamp":"2026-05-07T13:40:01.000Z","type":"response_item","payload":{"type":"function_call","name":"exec_command","arguments":{{Json("{\"cmd\":" + Json(cmd) + ",\"workdir\":\"/work/app\",\"yield_time_ms\":1000}")}},"call_id":"{{{callId}}}"}}""";

    static string FunctionCallOutput(string callId, string output) =>
        $$$"""{"timestamp":"2026-05-07T13:40:02.000Z","type":"response_item","payload":{"type":"function_call_output","call_id":"{{{callId}}}","output":{{{Json(output)}}}}}""";

    static string ExecCommandEnd(string callId, string script, string output, int exitCode) =>
        $$$"""{"timestamp":"2026-05-07T13:40:03.000Z","type":"event_msg","payload":{"type":"exec_command_end","call_id":"{{{callId}}}","process_id":"61553","turn_id":"t1","command":["/bin/zsh","-lc",{{{Json(script)}}}],"cwd":"/work/app","source":"unified_exec_startup","stdout":"","stderr":"","aggregated_output":{{{Json(output)}}},"exit_code":{{{exitCode}}},"duration":{"secs":2,"nanos":0},"formatted_output":"","status":"{{{(exitCode == 0 ? "completed" : "failed")}}}"}}""";

    static string ItemCompleted(string itemId, string script, string output, int exitCode) =>
        $$$"""{"timestamp":"2026-11-10T09:00:00.000Z","type":"event_msg","payload":{"type":"item_completed","thread_id":"th1","turn_id":"t1","item":{"type":"CommandExecution","id":"{{{itemId}}}","process_id":"4242","command":["/bin/zsh","-lc",{{{Json(script)}}}],"cwd":"/work/app","parsed_cmd":[{"type":"unknown","cmd":{{{Json(script)}}}}],"source":"unified_exec_startup","status":"completed","aggregated_output":{{{Json(output)}}},"exit_code":{{{exitCode}}},"duration":{"secs":1,"nanos":0}},"started_at_ms":1,"completed_at_ms":2}}""";

    static IReadOnlyList<FoundPullRequest> Observe(params string[] lines) {
        var evidence = new CreatedPullRequestEvidence();
        foreach (var line in lines) CodexCreatedPullRequestAdapter.Instance.Observe(line, evidence);
        return evidence.Drain();
    }

    // The call's own output says only that the process is still running; the URL is on exec_command_end.
    [Test]
    public async Task A_create_that_outlived_its_yield_is_found_from_its_exec_command_end() {
        var found = Observe(
            FunctionCall("call_pr", "gh pr create --draft --fill --base main"),
            FunctionCallOutput("call_pr", "Chunk ID: 1\nWall time: 1.0 seconds\nProcess running with session ID 61553\nOutput:\n"),
            ExecCommandEnd("call_pr", "gh pr create --draft --fill --base main", Url + "\n", 0));

        await Assert.That(found).IsEquivalentTo([new FoundPullRequest(new("kurrent-io", "kcap", 1945, Url), "call_pr")]);
    }

    [Test]
    public async Task A_create_from_a_command_execution_item_is_found() {
        var found = Observe(ItemCompleted("exec-7", "git push -u origin HEAD && gh pr create --fill", "Creating pull request\n\n" + Url + "\n", 0));

        await Assert.That(found.Select(f => f.CallId ?? "")).IsEquivalentTo(["exec-7"]);
    }

    [Test]
    public async Task A_create_gh_refused_because_the_pr_exists_is_not_found() {
        const string output = "a pull request for branch \"feature\" into branch \"main\" already exists:\n" + Url + "\n";

        await Assert.That(Observe(ExecCommandEnd("call_pr", "gh pr create --fill", output, 1))).IsEmpty();
        await Assert.That(Observe(ItemCompleted("exec-7", "gh pr create --fill", output, 1))).IsEmpty();
    }

    // A later write_stdin poll's output is not tied to the create, and an edit prints the URL of a PR it did not open.
    [Test]
    public async Task A_url_from_another_command_or_in_prose_is_not_found() {
        var found = Observe(
            FunctionCallOutput("call_poll", Url + "\n"),
            ExecCommandEnd("call_edit", "gh pr edit 1945 --title t", Url + "\n", 0),
            ExecCommandEnd("call_echo", "echo 'then run gh pr create'", Url + "\n", 0),
            $$$"""{"type":"event_msg","payload":{"type":"agent_message","message":"Opened {{{Url}}}"}}""");

        await Assert.That(found).IsEmpty();
    }

    [Test]
    public async Task A_malformed_line_is_ignored() {
        await Assert.That(Observe("""{"type":"event_msg","payload":{"type":"exec_command_end" """)).IsEmpty();
    }
}
