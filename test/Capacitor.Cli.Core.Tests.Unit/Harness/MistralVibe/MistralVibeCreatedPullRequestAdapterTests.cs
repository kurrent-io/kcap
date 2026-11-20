using System.Text.Json;
using Capacitor.Cli.Core.Harness.MistralVibe;
using Capacitor.Cli.Core.RepoEvidence;

namespace Capacitor.Cli.Core.Tests.Unit.Harness.MistralVibe;

public class MistralVibeCreatedPullRequestAdapterTests {
    const string Url = "https://github.com/kurrent-io/kcap/pull/1945";

    static string Json(string value) => JsonSerializer.Serialize(value);

    static string Effect(string id, string command, string stdout, int exitCode, string codeKey = "exit_code", string status = "completed") =>
        "{\"createdAt\":1791497382214,\"detail\":{\"display\":{\"message\":\"file_system.bash\",\"summary\":\"file_system.bash\"},\"input\":{\"command\":" + Json(command)
      + "},\"kind\":\"tool\",\"toolName\":\"file_system.bash\"},\"generationStatus\":\"completed\",\"id\":\"" + id + "\",\"sessionId\":\"66f6af87\",\"state\":{\"approvalSource\":\"bypass\","
      + "\"display\":{\"message\":\"file_system.bash\",\"success\":true},\"output\":{\"_meta\":{},\"content\":[],\"structured_content\":{\"command\":" + Json(command) + ",\""
      + codeKey + "\":" + exitCode + ",\"stderr\":\"\",\"stdout\":" + Json(stdout) + ",\"was_truncated\":false},\"type\":\"success\"},\"outputText\":\"\",\"status\":\"" + status + "\"},\"type\":\"effect\"}";

    static string LegacyCall(string id, string command) =>
        "{\"role\":\"assistant\",\"content\":\"Opening the PR.\",\"tool_calls\":[{\"id\":\"" + id + "\",\"index\":0,\"function\":{\"name\":\"bash\",\"arguments\":"
      + Json("{\"command\":" + Json(command) + "}") + "},\"type\":\"function\"}]}";

    static string LegacyResult(string id, string stdout, int returnCode, bool truncated = false) =>
        "{\"role\":\"tool\",\"tool_call_id\":\"" + id + "\",\"name\":\"bash\",\"content\":"
      + Json("command: gh pr create --fill\nstdout: " + stdout + "\nstderr: \nreturncode: " + returnCode + "\nwas_truncated: " + (truncated ? "true" : "false")) + "}";

    static IReadOnlyList<FoundPullRequest> Observe(params string[] lines) {
        var evidence = new CreatedPullRequestEvidence();
        foreach (var line in lines) MistralVibeCreatedPullRequestAdapter.Instance.Observe(line, evidence);
        return evidence.Drain();
    }

    [Test]
    public async Task A_settled_effect_create_is_found() {
        var found = Observe(Effect("effect-pr", "git push -u origin HEAD && gh pr create --fill", Url + "\n", 0));

        await Assert.That(found).IsEquivalentTo([new FoundPullRequest(new("kurrent-io", "kcap", 1945, Url), "effect-pr")]);
    }

    [Test]
    public async Task An_older_effect_reporting_returncode_is_read() {
        await Assert.That(Observe(Effect("effect-pr", "gh pr create --fill", Url, 0, codeKey: "returncode")).Count).IsEqualTo(1);
    }

    [Test]
    public async Task A_legacy_call_paired_with_its_tool_message_is_found() {
        var found = Observe(LegacyCall("call_pr", "gh pr create --fill"), LegacyResult("call_pr", Url, 0));

        await Assert.That(found.Select(f => f.CallId ?? "")).IsEquivalentTo(["call_pr"]);
    }

    // Vibe returns a non-zero exit as a result rather than an error.
    [Test]
    public async Task A_create_gh_refused_because_the_pr_exists_is_not_found() {
        const string output = "a pull request for branch \"feature\" into branch \"main\" already exists:\n" + Url + "\n";

        await Assert.That(Observe(Effect("e1", "gh pr create --fill 2>&1", output, 1))).IsEmpty();
        await Assert.That(Observe(LegacyCall("c1", "gh pr create --fill 2>&1"), LegacyResult("c1", output, 1))).IsEmpty();
    }

    [Test]
    public async Task A_url_from_another_command_or_in_prose_is_not_found() {
        var found = Observe(
            Effect("e1", "gh pr view --json url -q .url", Url, 0),
            Effect("e2", "echo 'gh pr create --fill'", Url, 0),
            LegacyResult("c9", Url, 0),
            "{\"type\":\"message\",\"id\":\"m1\",\"role\":\"assistant\",\"content\":\"Opened " + Url + " with bash\"}");

        await Assert.That(found).IsEmpty();
    }

    [Test]
    public async Task An_effect_without_an_exit_code_or_still_running_is_not_found() {
        const string noCode = "{\"type\":\"effect\",\"id\":\"e1\",\"detail\":{\"kind\":\"shell\",\"toolName\":\"file_system.bash\",\"input\":{\"command\":\"gh pr create --fill\"}},"
                            + "\"state\":{\"status\":\"completed\",\"output\":{\"stdout\":\"" + Url + "\",\"stderr\":\"\"},\"outputText\":\"" + Url + "\"}}";

        await Assert.That(Observe(noCode, Effect("e2", "gh pr create --fill", Url, 0, status: "running"))).IsEmpty();
    }

    // Truncated output may have dropped the line that really came last.
    [Test]
    public async Task A_truncated_output_is_not_read() {
        var truncated = Effect("e1", "gh pr create --fill", Url, 0).Replace("\"was_truncated\":false", "\"was_truncated\":true");

        await Assert.That(Observe(truncated)).IsEmpty();
        await Assert.That(Observe(LegacyCall("c1", "gh pr create --fill"), LegacyResult("c1", Url, 0, truncated: true))).IsEmpty();
    }

    // A subagent's store is read by a child watcher with this same adapter.
    [Test]
    public async Task A_subagents_create_is_found_the_same_way() {
        await Assert.That(Observe(Effect("effect-child", "gh pr create --fill", Url, 0)).Count).IsEqualTo(1);
    }

    [Test]
    public async Task A_malformed_line_is_ignored() {
        await Assert.That(Observe("{\"type\":\"effect\",\"detail\":{\"toolName\":\"file_system.bash\"")).IsEmpty();
    }
}
