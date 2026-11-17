using System.Text.Json;
using Capacitor.Cli.Core.Harness.Kiro;
using Capacitor.Cli.Core.RepoEvidence;

namespace Capacitor.Cli.Core.Tests.Unit.Harness.Kiro;

public class KiroCreatedPullRequestAdapterTests {
    const string Url = "https://github.com/kurrent-io/kcap/pull/1945";

    static string Json(string value) => JsonSerializer.Serialize(value);

    static string ToolResults(string toolUseId, string command, string stdout, int exitCode, string stderr = "") =>
        "{\"version\":\"v1\",\"kind\":\"ToolResults\",\"data\":{\"message_id\":\"m1\",\"content\":[{\"kind\":\"toolResult\",\"data\":{\"toolUseId\":\"" + toolUseId
      + "\",\"content\":[{\"kind\":\"json\",\"data\":{\"exit_status\":\"exit status: " + exitCode + "\",\"stdout\":" + Json(stdout) + ",\"stderr\":" + Json(stderr) + "}}],\"status\":\"success\"}}],"
      + "\"results\":{\"" + toolUseId + "\":{\"tool\":{\"tool_use_purpose\":\"Open the PR\",\"kind\":{\"BuiltIn\":{\"ExecuteCmd\":{\"command\":" + Json(command) + ",\"working_dir\":null}}}},"
      + "\"result\":{\"Success\":{\"items\":[{\"Json\":{\"exit_status\":\"exit status: " + exitCode + "\",\"stdout\":" + Json(stdout) + ",\"stderr\":" + Json(stderr) + "}}]}}}}}}";

    static IReadOnlyList<FoundPullRequest> Observe(params string[] lines) {
        var evidence = new CreatedPullRequestEvidence();
        foreach (var line in lines) KiroCreatedPullRequestAdapter.Instance.Observe(line, evidence);
        return evidence.Drain();
    }

    [Test]
    public async Task A_create_whose_stdout_ends_with_the_url_is_found() {
        var found = Observe(ToolResults("tooluse_pr", "git push -u origin HEAD && gh pr create --fill", Url + "\n", 0));

        await Assert.That(found).IsEquivalentTo([new FoundPullRequest(new("kurrent-io", "kcap", 1945, Url), "tooluse_pr")]);
    }

    // Kiro calls a command that ran a Success whatever its exit status.
    [Test]
    public async Task A_create_gh_refused_because_the_pr_exists_is_not_found() {
        var found = Observe(ToolResults("tooluse_pr", "gh pr create --fill 2>&1", "a pull request for branch \"feature\" into branch \"main\" already exists:\n" + Url + "\n", 1));

        await Assert.That(found).IsEmpty();
    }

    [Test]
    public async Task A_url_from_another_command_or_in_prose_is_not_found() {
        var found = Observe(
            ToolResults("t1", "gh pr view --json url -q .url", Url + "\n", 0),
            ToolResults("t2", "echo 'gh pr create --fill'", Url + "\n", 0),
            "{\"version\":\"v1\",\"kind\":\"AssistantMessage\",\"data\":{\"content\":[{\"kind\":\"text\",\"data\":\"Opened " + Url + "\"}]}}");

        await Assert.That(found).IsEmpty();
    }

    // A crew child is its own kiro-cli session with its own top-level watcher; its create is read the same way and
    // links to that child session.
    [Test]
    public async Task A_crew_childs_create_is_found_the_same_way() {
        await Assert.That(Observe(ToolResults("tooluse_child", "gh pr create --fill", Url, 0)).Count).IsEqualTo(1);
    }

    [Test]
    public async Task A_malformed_line_is_ignored() {
        await Assert.That(Observe("{\"kind\":\"ToolResults\",\"data\":{\"results\":{\"x\":{\"tool\":{\"kind\":{\"BuiltIn\":{\"ExecuteCmd\"")).IsEmpty();
    }
}
