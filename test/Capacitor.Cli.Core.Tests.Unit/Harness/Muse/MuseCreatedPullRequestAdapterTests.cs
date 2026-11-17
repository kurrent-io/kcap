using System.Text.Json;
using Capacitor.Cli.Core.Harness.Muse;
using Capacitor.Cli.Core.RepoEvidence;

namespace Capacitor.Cli.Core.Tests.Unit.Harness.Muse;

public class MuseCreatedPullRequestAdapterTests {
    const string Url = "https://github.com/kurrent-io/kcap/pull/1945";

    static string Json(string value) => JsonSerializer.Serialize(value);

    static string ShellReport(string command, string output, int exitCode, string status = "completed") =>
        JsonSerializer.Serialize(new Dictionary<string, object> {
            ["chunk_id"] = "exec-1-1", ["command"] = command, ["description"] = "Open the PR", ["exit_code"] = exitCode,
            ["terminal_status"] = status, ["output"] = output, ["original_output_bytes"] = output.Length, ["truncated"] = false,
        });

    static string Batch(string callId, string resultText) =>
        "{\"schema_version\":1,\"id\":\"ccc213d1-33f1-474d-9b73-87b463e34d9e\",\"stream\":{\"kind\":\"session\",\"id\":\"01a13694-b0ad-7ae2-8bf5-2af6357a0847\"},\"sequence\":78,"
      + "\"recorded_at\":1791917081826392,\"record_type\":\"event\",\"durability\":\"durable\",\"causation_id\":null,\"payload_type\":\"runtime.session\",\"payload_schema_version\":1,"
      + "\"payload\":{\"kind\":\"run\",\"run_id\":\"r1\",\"event\":{\"kind\":\"tool_result_batch_committed\",\"batch_id\":\"b1\",\"results\":[{\"tool_call_index\":0,\"tool_call_id\":\""
      + callId + "\",\"text\":" + Json(resultText) + "}]},\"source_run_record_id\":\"s1\",\"source_run_record_sequence\":33}}";

    static string RetainedFrame(params string[] records) =>
        "{\"retained_frame\":{\"frame_id\":\"f1\"},\"children\":[" + string.Join(",", records.Select(r => "{\"record_json\":" + Json(r) + "}")) + "]}";

    static IReadOnlyList<FoundPullRequest> Observe(params string[] lines) {
        var evidence = new CreatedPullRequestEvidence();
        foreach (var line in lines) MuseCreatedPullRequestAdapter.Instance.Observe(line, evidence);
        return evidence.Drain();
    }

    [Test]
    public async Task A_create_whose_report_ends_with_the_url_is_found() {
        var found = Observe(Batch("call_pr", ShellReport("git push -u origin HEAD && gh pr create --fill", Url + "\n", 0)));

        await Assert.That(found).IsEquivalentTo([new FoundPullRequest(new("kurrent-io", "kcap", 1945, Url), "call_pr")]);
    }

    [Test]
    public async Task A_create_inside_a_retained_frame_is_found() {
        var found = Observe(RetainedFrame("{\"retained_marker\":true}", Batch("call_pr", ShellReport("gh pr create --fill", Url, 0))));

        await Assert.That(found.Select(f => f.CallId ?? "")).IsEquivalentTo(["call_pr"]);
    }

    [Test]
    public async Task A_create_gh_refused_because_the_pr_exists_is_not_found() {
        var output = "a pull request for branch \"feature\" into branch \"main\" already exists:\n" + Url + "\n";

        await Assert.That(Observe(Batch("c1", ShellReport("gh pr create --fill 2>&1", output, 1, status: "failed")))).IsEmpty();
        await Assert.That(Observe(Batch("c2", ShellReport("gh pr create --fill 2>&1", output, 1)))).IsEmpty();
    }

    [Test]
    public async Task A_url_from_another_command_or_in_prose_is_not_found() {
        var found = Observe(
            Batch("c1", ShellReport("gh pr view --json url -q .url", Url, 0)),
            Batch("c2", ShellReport("echo 'gh pr create --fill'", Url, 0)),
            Batch("c3", "Opened " + Url + " with gh pr create"));

        await Assert.That(found).IsEmpty();
    }

    // Truncated output may have dropped the line that really came last.
    [Test]
    public async Task A_truncated_output_is_not_read() {
        var report = ShellReport("gh pr create --fill", Url, 0).Replace("\"truncated\":false", "\"truncated\":true");

        await Assert.That(Observe(Batch("c1", report))).IsEmpty();
    }

    // A subagent writes subagent/<id>/session.jsonl, read by a child watcher with this same adapter.
    [Test]
    public async Task A_subagents_create_is_found_the_same_way() {
        await Assert.That(Observe(Batch("call_child", ShellReport("gh pr create --fill", Url, 0))).Count).IsEqualTo(1);
    }

    [Test]
    public async Task A_malformed_line_is_ignored() {
        await Assert.That(Observe("{\"payload\":{\"event\":{\"kind\":\"tool_result_batch_committed\"")).IsEmpty();
    }
}
