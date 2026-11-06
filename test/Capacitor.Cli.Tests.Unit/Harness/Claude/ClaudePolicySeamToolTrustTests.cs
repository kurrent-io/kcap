namespace Capacitor.Cli.Tests.Unit.Harness.Claude;

using System.Text.Json.Nodes;
using Capacitor.Cli.Core;
using Capacitor.Cli.Harness.Claude;
using Capacitor.Cli.Tests.Unit.Policy;

public class ClaudePolicySeamToolTrustTests {
    [TempDir] public required TempDir Tmp { get; init; }
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    const string Sid = "9dc2775376454e4691ecc2d69973c152";
    const string Merge = "mcp__plugin_kcap_kcap-workitems__merge_work_item";

    ClaudePolicySeam Seam => new(Config.Root, TimeProvider.System, new HookSpool(Config.Root, TimeProvider.System, server: null));

    string Body(string toolName, string? permissionMode) {
        var node = new JsonObject {
            ["hook_event_name"] = "PreToolUse", ["session_id"] = Sid,
            ["tool_name"] = toolName, ["tool_input"] = new JsonObject(),
            ["cwd"] = Tmp.PathTo("repo"),
        };
        if (permissionMode is not null) node["permission_mode"] = permissionMode;
        return node.ToJsonString();
    }

    void WriteUserPolicy(string yaml) => File.WriteAllText(Config.Root.Path("approvals.yaml"), yaml);

    async Task<string?> DecisionFor(string toolName, string? permissionMode = "auto", bool rendered = false) {
        var stdout = new StringWriter();
        await Seam.HandlePreToolUseAsync(Body(toolName, permissionMode), Sid, rendered, stdout);
        var output = stdout.ToString();
        return output.Length == 0
            ? null
            : JsonNode.Parse(output)!["hookSpecificOutput"]!["permissionDecision"]!.GetValue<string>();
    }

    [Test]
    [Arguments(Merge)]
    [Arguments("mcp__plugin_kcap_kcap-plans__update_plan_task")]
    [Arguments("mcp__plugin_kcap_kcap-review__get_pr_summary")]
    [Arguments("mcp__plugin_kcap_kcap-sessions__search_sessions")]
    [Arguments("mcp__plugin_kcap_kcap-handoff__continue_session")]
    [Arguments("mcp__plugin_kcap_kcap-analytics__query_analytics")]
    public async Task Every_tool_of_a_server_kcap_auto_approves_is_allowed_in_auto_mode(string tool) =>
        await Assert.That(await DecisionFor(tool)).IsEqualTo("allow");

    [Test]
    [Arguments("mcp__plugin_kcap_kcap-knowledge__list_skills")]
    [Arguments("mcp__plugin_kcap_kcap-memory__search_memories")]
    [Arguments("mcp__plugin_kcap_kcap-pages__get_page")]
    [Arguments("mcp__plugin_kcap_kcap-flows__list_flow_definitions")]
    public async Task A_read_on_any_other_kcap_server_is_allowed_in_auto_mode(string tool) =>
        await Assert.That(await DecisionFor(tool)).IsEqualTo("allow");

    [Test]
    [Arguments("mcp__plugin_kcap_kcap-pages__publish_page")]
    [Arguments("mcp__plugin_kcap_kcap-memory__save_memory")]
    [Arguments("mcp__plugin_kcap_kcap-knowledge__curate_fact_cluster")]
    [Arguments("mcp__plugin_kcap_kcap-flows__start_flow")]
    public async Task A_write_on_any_other_kcap_server_stays_with_the_classifier(string tool) =>
        await Assert.That(await DecisionFor(tool)).IsNull();

    /// <summary>The bare form is the name a repository's own <c>.mcp.json</c> can claim, so trusting
    /// it would auto-approve whatever server a cloned repository registers under it.</summary>
    [Test]
    [Arguments("mcp__kcap-workitems__merge_work_item")]
    [Arguments("mcp__plugin_other_kcap-workitems__merge_work_item")]
    [Arguments("mcp__plugin_kcap_kcap-unknown__read")]
    [Arguments("mcp__plugin_linear_linear__save_issue")]
    [Arguments("Bash")]
    public async Task A_tool_kcap_does_not_ship_is_left_alone(string tool) =>
        await Assert.That(await DecisionFor(tool)).IsNull();

    [Test]
    [Arguments("default")]
    [Arguments("acceptEdits")]
    [Arguments("plan")]
    [Arguments("dontAsk")]
    [Arguments("bypassPermissions")]
    [Arguments(null)]
    public async Task Only_auto_mode_is_trusted(string? mode) =>
        await Assert.That(await DecisionFor(Merge, mode)).IsNull();

    /// <summary>A rendered session runs tighten-only, so rule allows do not reach it. Trust has to,
    /// or a hosted auto-mode session stays blocked by the classifier.</summary>
    [Test]
    public async Task A_rendered_session_is_trusted_too() =>
        await Assert.That(await DecisionFor(Merge, rendered: true)).IsEqualTo("allow");

    [Test]
    [Arguments("deny", false)]
    [Arguments("deny", true)]
    [Arguments("ask", false)]
    [Arguments("ask", true)]
    public async Task A_policy_rule_outranks_trust(string outcome, bool rendered) {
        WriteUserPolicy($"version: 1\nrules:\n  - match: {{ kind: mcp_tool, server: plugin_kcap_kcap-workitems, tool: merge_work_item }}\n    outcome: {outcome}\n");
        await Assert.That(await DecisionFor(Merge, rendered: rendered)).IsEqualTo(outcome);
    }

    [Test]
    public async Task A_governed_session_is_trusted_where_no_rule_decides() {
        WriteUserPolicy("version: 1\nrules:\n  - match: { kind: shell, command: \"git push --force*\" }\n    outcome: deny\n");
        await Assert.That(await DecisionFor(Merge)).IsEqualTo("allow");
        await Assert.That(SpooledPolicyEvents.Decisions(Config.Root, Sid).Count).IsEqualTo(0);
    }
}
