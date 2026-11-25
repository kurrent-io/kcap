using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Acp;
using Capacitor.Cli.Daemon.Harness.Codex;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Tests.Unit.Harness.Codex;

/// <summary>
/// <see cref="CodexApprovalBridge"/> forwards a server-initiated <c>*/requestApproval</c> to an injected
/// "ask the user" delegate and maps the decision back to codex's wire shape — always fail-closed. Tested
/// against the delegate directly (no SignalR). The security invariant: only a recognized affirmative
/// outcome carrying a known option id grants; everything else denies.
/// </summary>
public class CodexApprovalBridgeTests {
    const string AgentId  = "agent-1";
    static readonly TimeSpan LongTimeout = TimeSpan.FromSeconds(30);

    static CodexApprovalBridge Bridge(
            Func<AcpInteractionRequest, CancellationToken, Task<AcpInteractionDecision>> requestInteraction,
            TimeSpan? timeout = null) =>
        new(requestInteraction, AgentId, NullLogger.Instance, timeout ?? LongTimeout, TimeProvider.System);

    static CodexApprovalBridge Deciding(AcpInteractionDecision decision, TimeSpan? timeout = null) =>
        Bridge((_, _) => Task.FromResult(decision), timeout);

    static AcpRequest ApprovalRequest(string method, string paramsJson) =>
        new(1, method, JsonElement.Parse(paramsJson));

    const string CommandMethod = "item/commandExecution/requestApproval";
    const string FileMethod    = "item/fileChange/requestApproval";
    const string PermsMethod   = "item/permissions/requestApproval";
    const string CommandParams = """{"threadId":"thread-1","turnId":"turn-1","itemId":"item-1"}""";

    static async Task<string?> Decision(JsonElement? result) =>
        await Task.FromResult(result is { } r && r.TryGetProperty("decision", out var d) ? d.GetString() : null);

    // ── The grant paths (positive controls) ────────────────────────────────────────────────────
    [Test]
    public async Task Command_accept_maps_to_decision_accept() {
        var bridge = Deciding(new AcpInteractionDecision("allow", "accept", "Allow", null, null, null));
        var result = await bridge.HandleAsync(ApprovalRequest(CommandMethod, CommandParams), CancellationToken.None);
        await Assert.That(await Decision(result)).IsEqualTo("accept");
    }

    [Test]
    public async Task Command_accept_for_session_maps_to_decision_acceptForSession() {
        var bridge = Deciding(new AcpInteractionDecision("allow_always", "acceptForSession", "Allow for session", null, null, null));
        var result = await bridge.HandleAsync(ApprovalRequest(CommandMethod, CommandParams), CancellationToken.None);
        await Assert.That(await Decision(result)).IsEqualTo("acceptForSession");
    }

    [Test]
    public async Task File_change_accept_maps_to_decision_accept() {
        // The file-change method name is by-convention; the suffix match must still route it.
        var bridge = Deciding(new AcpInteractionDecision("allow", "accept", "Allow", null, null, null));
        var result = await bridge.HandleAsync(ApprovalRequest(FileMethod, CommandParams), CancellationToken.None);
        await Assert.That(await Decision(result)).IsEqualTo("accept");
    }

    // ── Fail-closed paths ───────────────────────────────────────────────────────────────────────
    [Test]
    public async Task Deny_outcome_maps_to_decline() {
        var bridge = Deciding(new AcpInteractionDecision("deny", null, null, null, null, null));
        var result = await bridge.HandleAsync(ApprovalRequest(CommandMethod, CommandParams), CancellationToken.None);
        await Assert.That(await Decision(result)).IsEqualTo("decline");
    }

    [Test]
    public async Task Affirmative_outcome_with_unknown_option_id_declines() {
        // Fail-safe: an affirmative outcome whose SelectedOptionId is NOT one we offered must never grant.
        var bridge = Deciding(new AcpInteractionDecision("allow", "somethingElse", null, null, null, null));
        var result = await bridge.HandleAsync(ApprovalRequest(CommandMethod, CommandParams), CancellationToken.None);
        await Assert.That(await Decision(result)).IsEqualTo("decline");
    }

    [Test]
    public async Task Affirmative_outcome_with_null_option_id_declines() {
        var bridge = Deciding(new AcpInteractionDecision("allow", null, null, null, null, null));
        var result = await bridge.HandleAsync(ApprovalRequest(CommandMethod, CommandParams), CancellationToken.None);
        await Assert.That(await Decision(result)).IsEqualTo("decline");
    }

    [Test]
    public async Task Missing_thread_id_declines_without_asking() {
        var asked = false;
        var bridge = Bridge((_, _) => { asked = true; return Task.FromResult(new AcpInteractionDecision("allow", "accept", null, null, null, null)); });
        var result = await bridge.HandleAsync(ApprovalRequest(CommandMethod, """{"itemId":"item-1"}"""), CancellationToken.None);
        await Assert.That(await Decision(result)).IsEqualTo("decline");
        await Assert.That(asked).IsFalse(); // never correlated → never forwarded to a human
    }

    [Test]
    public async Task Timeout_declines() {
        var bridge = Bridge(async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return default; }, TimeSpan.FromMilliseconds(50));
        var result = await bridge.HandleAsync(ApprovalRequest(CommandMethod, CommandParams), CancellationToken.None);
        await Assert.That(await Decision(result)).IsEqualTo("decline");
    }

    [Test]
    public async Task Oversized_timeout_is_clamped_and_does_not_throw() {
        // A misconfigured huge timeout must not throw at CancelAfter (which caps ~24.8 days) — the ctor
        // clamps it, so the normal accept path still works.
        var bridge = Bridge((_, _) => Task.FromResult(new AcpInteractionDecision("allow", "accept", null, null, null, null)),
            timeout: TimeSpan.FromDays(100));
        var result = await bridge.HandleAsync(ApprovalRequest(CommandMethod, CommandParams), CancellationToken.None);
        await Assert.That(await Decision(result)).IsEqualTo("accept");
    }

    [Test]
    public async Task Delegate_throwing_declines_never_errors() {
        var bridge = Bridge((_, _) => throw new InvalidOperationException("boom"));
        var result = await bridge.HandleAsync(ApprovalRequest(CommandMethod, CommandParams), CancellationToken.None);
        await Assert.That(await Decision(result)).IsEqualTo("decline"); // valid body, not a JSON-RPC error
    }

    // ── Permissions grant shape ({permissions,scope}, not {decision}) ────────────────────────────
    [Test]
    public async Task Permissions_grant_accept_echoes_requested_profile() {
        var bridge = Deciding(new AcpInteractionDecision("allow", "accept", null, null, null, null));
        const string paramsJson = """{"threadId":"thread-1","itemId":"item-1","permissions":{"network":{"allowed":true}}}""";
        var result = await bridge.HandleAsync(ApprovalRequest(PermsMethod, paramsJson), CancellationToken.None);

        await Assert.That(result).IsNotNull();
        var r = result!.Value;
        await Assert.That(r.TryGetProperty("decision", out _)).IsFalse();
        await Assert.That(r.GetProperty("scope").GetString()).IsEqualTo("turn");
        await Assert.That(r.GetProperty("permissions").GetProperty("network").GetProperty("allowed").GetBoolean()).IsTrue();
    }

    [Test]
    public async Task Permissions_grant_deny_returns_empty_grant_not_a_decision() {
        var bridge = Deciding(new AcpInteractionDecision("deny", null, null, null, null, null));
        const string paramsJson = """{"threadId":"thread-1","itemId":"item-1","permissions":{"network":{"allowed":true}}}""";
        var result = await bridge.HandleAsync(ApprovalRequest(PermsMethod, paramsJson), CancellationToken.None);

        var r = result!.Value;
        await Assert.That(r.TryGetProperty("decision", out _)).IsFalse();          // never the malformed decision shape
        await Assert.That(r.GetProperty("permissions").EnumerateObject().Any()).IsFalse(); // empty grant
        await Assert.That(r.GetProperty("scope").GetString()).IsEqualTo("turn");
    }

    [Test]
    public async Task Permissions_grant_accept_over_non_object_profile_falls_to_empty_deny() {
        // Even an affirmative decision must not grant a profile we can't read back as an object — echoing
        // {} at the affirmative "session" scope could over-grant if the server treats it as defaults.
        var bridge = Deciding(new AcpInteractionDecision("allow", "accept", null, null, null, null));
        const string paramsJson = """{"threadId":"thread-1","itemId":"item-1","permissions":"all"}"""; // non-object profile
        var result = await bridge.HandleAsync(ApprovalRequest(PermsMethod, paramsJson), CancellationToken.None);

        var r = result!.Value;
        await Assert.That(r.GetProperty("permissions").EnumerateObject().Any()).IsFalse(); // empty, not the string
        await Assert.That(r.GetProperty("scope").GetString()).IsEqualTo("turn");           // deny scope, never "session"
    }

    // ── What the person approving is shown ───────────────────────────────────────────────────────
    static async Task<(AcpInteractionRequest Shown, string? Decision)> Show(
            string method, string paramsJson, Action<CodexApprovalBridge>? observe = null) {
        AcpInteractionRequest? shown = null;
        var bridge = Bridge((request, _) => {
            shown = request;
            return Task.FromResult(new AcpInteractionDecision("allow", "accept", "Allow", null, null, null));
        });
        observe?.Invoke(bridge);
        var result = await bridge.HandleAsync(ApprovalRequest(method, paramsJson), CancellationToken.None);
        return (shown!.Value, await Decision(result));
    }

    static string? Input(AcpInteractionRequest shown, string property) => shown.ToolInput?.Str(property);

    static JsonElement Changes(string json) => JsonElement.Parse(json);

    /// The card reads a command the way Codex's hook names it, and the answer still goes back on the request.
    [Test]
    public async Task A_command_approval_shows_the_script_it_runs_as_a_shell_command() {
        var (shown, decision) = await Show(CommandMethod, """
            {"threadId":"thread-1","turnId":"turn-1","itemId":"item-1","startedAtMs":1,
             "command":"/bin/bash -lc 'touch kcap-permission-check'","cwd":"/wt","reason":"Create the marker"}
            """);

        await Assert.That(shown.ToolName).IsEqualTo("Bash");
        await Assert.That(Input(shown, "command")).IsEqualTo("touch kcap-permission-check");
        await Assert.That(Input(shown, "cwd")).IsEqualTo("/wt");
        await Assert.That(Input(shown, "description")).IsEqualTo("Create the marker");
        await Assert.That(shown.ToolCallId).IsEqualTo("item-1");
        await Assert.That(decision).IsEqualTo("accept");
    }

    /// Input for a running command, a network grant or another environment asks for more than the command text says.
    [Test]
    [Arguments("""{"threadId":"thread-1","itemId":"item-1","command":"cat","kind":"writeStdin"}""")]
    [Arguments("""{"threadId":"thread-1","itemId":"item-1","command":"curl x","networkApprovalContext":{"host":"x","protocol":"https"}}""")]
    [Arguments("""{"threadId":"thread-1","itemId":"item-1","command":"ls","environmentId":"remote"}""")]
    public async Task A_command_approval_asking_for_more_than_its_command_is_shown_as_it_came(string paramsJson) {
        var (shown, _) = await Show(CommandMethod, paramsJson);

        await Assert.That(shown.ToolName).IsEqualTo("commandExecution");
        await Assert.That(Input(shown, "threadId")).IsEqualTo("thread-1");
    }

    [Test]
    public async Task A_one_file_edit_shows_the_file_and_its_lines() {
        var (shown, decision) = await Show(FileMethod, """{"threadId":"thread-1","itemId":"fc-1","reason":"Fix the greeting"}""",
            bridge => bridge.FileChanges.Observe("fc-1", Changes("""
                [{"path":"/wt/src/a.txt","kind":{"type":"update","move_path":null},"diff":"@@ -1,2 +1,2 @@\n keep\n-hello\n+goodbye\n"}]
                """)));

        await Assert.That(shown.ToolName).IsEqualTo("Edit");
        await Assert.That(Input(shown, "file_path")).IsEqualTo("/wt/src/a.txt");
        await Assert.That(Input(shown, "old_string")).IsEqualTo("keep\nhello");
        await Assert.That(Input(shown, "new_string")).IsEqualTo("keep\ngoodbye");
        await Assert.That(Input(shown, "description")).IsEqualTo("Fix the greeting");
        await Assert.That(decision).IsEqualTo("accept");
    }

    [Test]
    public async Task An_edit_in_several_places_shows_each_one() {
        var (shown, _) = await Show(FileMethod, """{"threadId":"thread-1","itemId":"fc-1"}""",
            bridge => bridge.FileChanges.Observe("fc-1", Changes("""
                [{"path":"/wt/a.txt","kind":{"type":"update"},"diff":"--- a.txt\n+++ a.txt\n@@ -1 +1 @@\n-one\n+uno\n@@ -9 +9 @@\n-nine\n+nueve\n\\ No newline at end of file\n"}]
                """)));

        await Assert.That(shown.ToolName).IsEqualTo("MultiEdit");
        await Assert.That(Input(shown, "file_path")).IsEqualTo("/wt/a.txt");
        var edits = shown.ToolInput!.Value.GetProperty("edits").EnumerateArray()
            .Select(e => $"{e.Str("old_string")}>{e.Str("new_string")}");
        await Assert.That(string.Join("|", edits)).IsEqualTo("one>uno|nine>nueve");
    }

    [Test]
    public async Task A_new_file_shows_as_a_write_of_its_content() {
        var (shown, _) = await Show(FileMethod, """{"threadId":"thread-1","itemId":"fc-1"}""",
            bridge => bridge.FileChanges.Observe("fc-1", Changes("""[{"path":"/wt/new.txt","kind":{"type":"add"},"diff":"hello\nworld\n"}]""")));

        await Assert.That(shown.ToolName).IsEqualTo("Write");
        await Assert.That(Input(shown, "file_path")).IsEqualTo("/wt/new.txt");
        await Assert.That(Input(shown, "content")).IsEqualTo("hello\nworld\n");
    }

    static string ShownPaths(AcpInteractionRequest shown) =>
        string.Join(",", shown.ToolInput!.Value.GetProperty("changes").EnumerateArray().Select(c => c.Str("path")));

    /// A change one file cannot show — several files, a delete, a rename — is shown as the patch it is, naming every
    /// path it touches.
    [Test]
    [Arguments("""[{"path":"/wt/a.txt","kind":{"type":"update"},"diff":"@@ -1 +1 @@\n-a\n+b\n"},{"path":"/wt/b.txt","kind":{"type":"add"},"diff":"x"}]""", "/wt/a.txt,/wt/b.txt")]
    [Arguments("""[{"path":"/wt/gone.txt","kind":{"type":"delete"},"diff":"old\n"}]""", "/wt/gone.txt")]
    [Arguments("""[{"path":"/wt/a.txt","kind":{"type":"update","move_path":"/wt/b.txt"},"diff":""}]""", "/wt/a.txt")]
    public async Task A_change_one_file_cannot_show_is_shown_as_the_patch(string changes, string paths) {
        var (shown, _) = await Show(FileMethod, """{"threadId":"thread-1","itemId":"fc-1","reason":"Tidy up"}""",
            bridge => bridge.FileChanges.Observe("fc-1", Changes(changes)));

        await Assert.That(shown.ToolName).IsEqualTo("apply_patch");
        await Assert.That(Input(shown, "description")).IsEqualTo("Tidy up");
        await Assert.That(ShownPaths(shown)).IsEqualTo(paths);
    }

    /// An edit that only drops a final newline or turns CRLF into LF would read as no change in an Edit card, so the
    /// raw diff goes instead.
    static Task<(AcpInteractionRequest Shown, string? Decision)> ShowEdit(string diff) {
        var changes = new JsonArray(new JsonObject { ["path"] = "/wt/a.txt", ["kind"] = new JsonObject { ["type"] = "update" }, ["diff"] = diff });
        return Show(FileMethod, """{"threadId":"thread-1","itemId":"fc-1"}""",
            bridge => bridge.FileChanges.Observe("fc-1", Changes(changes.ToJsonString())));
    }

    [Test]
    [Arguments("@@ -1 +1 @@\n-hello\n\\ No newline at end of file\n+hello\n")]
    [Arguments("@@ -1,2 +1,2 @@\n-a\r\n+a\n b\n")]
    [Arguments("@@ -1 +1 @@\n-a\r\n+a\n")]
    public async Task An_edit_its_lines_cannot_show_carries_the_raw_diff(string diff) {
        var (shown, _) = await ShowEdit(diff);

        await Assert.That(shown.ToolName).IsEqualTo("apply_patch");
        await Assert.That(shown.ToolInput!.Value.GetProperty("changes")[0].Str("diff")).IsEqualTo(diff);
    }

    /// The control for the case above: a line ending changed along with the text still shows the text.
    [Test]
    public async Task An_edit_that_also_changes_a_line_ending_still_shows_its_lines() {
        var (shown, _) = await ShowEdit("@@ -1 +1 @@\n-a\r\n+b\n");

        await Assert.That(shown.ToolName).IsEqualTo("Edit");
        await Assert.That(Input(shown, "new_string")).IsEqualTo("b");
    }

    /// A grant root asks for writes under it for the rest of the session, so even a one-file edit names the root, with
    /// its scope at both ends of the description however long the root is.
    [Test]
    [Arguments("/wt")]
    [Arguments("/Users/someone/dev/a-rather-long-organisation/a-rather-long-repository/.capacitor/worktrees/happy-book")]
    public async Task A_grant_beyond_the_edit_names_its_root_and_how_long_it_lasts(string root) {
        var (shown, _) = await Show(FileMethod, $$"""{"threadId":"thread-1","itemId":"fc-1","reason":"Fix the build","grantRoot":"{{root}}"}""",
            bridge => bridge.FileChanges.Observe("fc-1", Changes($$"""[{"path":"{{root}}/a.txt","kind":{"type":"update"},"diff":"@@ -1 +1 @@\n-a\n+b\n"}]""")));

        await Assert.That(shown.ToolName).IsEqualTo("apply_patch");
        await Assert.That(Input(shown, "grant_root")).IsEqualTo(root);
        await Assert.That(Input(shown, "description")).IsEqualTo($"Write under {root} for the rest of the session");
        await Assert.That(Input(shown, "reason")).IsEqualTo("Fix the build");
        await Assert.That(ShownPaths(shown)).IsEqualTo($"{root}/a.txt");
    }

    /// Until an edit's changes are known the card cannot say what it is — an empty patch would read as no change — but
    /// a grant it asks for is still named.
    [Test]
    public async Task A_change_whose_files_are_not_yet_known_is_shown_as_it_came_naming_its_grant() {
        var (shown, _) = await Show(FileMethod, """{"threadId":"thread-1","itemId":"fc-1","reason":"Fix the build","grantRoot":"/wt"}""");

        await Assert.That(shown.ToolName).IsEqualTo("fileChange");
        await Assert.That(Input(shown, "itemId")).IsEqualTo("fc-1");
        await Assert.That(Input(shown, "grant_root")).IsEqualTo("/wt");
        await Assert.That(Input(shown, "description")).IsEqualTo("Write under /wt for the rest of the session");
        await Assert.That(Input(shown, "reason")).IsEqualTo("Fix the build");
    }

    // ── Non-approval requests keep the always-decline shapes ─────────────────────────────────────
    [Test]
    public async Task Elicitation_request_declines_with_action_shape() {
        var bridge = Deciding(new AcpInteractionDecision("allow", "accept", null, null, null, null));
        var result = await bridge.HandleAsync(ApprovalRequest("session/elicitation/create", """{"threadId":"t"}"""), CancellationToken.None);
        await Assert.That(result!.Value.GetProperty("action").GetString()).IsEqualTo("decline");
    }

    [Test]
    public async Task Unknown_server_request_declines_with_decision_shape() {
        var bridge = Deciding(new AcpInteractionDecision("allow", "accept", null, null, null, null));
        var result = await bridge.HandleAsync(ApprovalRequest("some/other/method", """{"threadId":"t"}"""), CancellationToken.None);
        await Assert.That(await Decision(result)).IsEqualTo("decline");
    }
}
