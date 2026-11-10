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

    static JsonObject WorkItemsPlugin => Server("plugin:kcap:kcap-workitems", "plugin");

    ClaudePolicySeam Seam => new(Config.Root, TimeProvider.System, new HookSpool(Config.Root, TimeProvider.System, server: null));

    static JsonObject Server(string name, string? source) => new() { ["name"] = name, ["source"] = source };

    string Body(string toolName, JsonNode? mcpServer, string? permissionMode) {
        var node = new JsonObject {
            ["hook_event_name"] = "PreToolUse", ["session_id"] = Sid,
            ["tool_name"] = toolName, ["tool_input"] = new JsonObject(),
            ["cwd"] = Tmp.PathTo("repo"),
        };
        if (mcpServer is not null) node["mcp_server"] = mcpServer;
        if (permissionMode is not null) node["permission_mode"] = permissionMode;
        return node.ToJsonString();
    }

    void WriteUserPolicy(string yaml) => File.WriteAllText(Config.Root.Path("approvals.yaml"), yaml);

    async Task<string?> DecisionFor(string toolName, JsonNode? mcpServer, string? permissionMode = "auto", bool rendered = false) {
        var stdout = new StringWriter();
        await Seam.HandlePreToolUseAsync(Body(toolName, mcpServer, permissionMode), Sid, rendered, stdout);
        var output = stdout.ToString();
        return output.Length == 0
            ? null
            : JsonNode.Parse(output)!["hookSpecificOutput"]!["permissionDecision"]!.GetValue<string>();
    }

    Task<string?> DecisionForPluginTool(string server, string tool) =>
        DecisionFor($"mcp__plugin_kcap_{server}__{tool}", Server($"plugin:kcap:{server}", "plugin"));

    [Test]
    [Arguments("kcap-workitems", "merge_work_item")]
    [Arguments("kcap-plans", "update_plan_task")]
    [Arguments("kcap-review", "get_pr_summary")]
    [Arguments("kcap-sessions", "search_sessions")]
    [Arguments("kcap-handoff", "continue_session")]
    [Arguments("kcap-analytics", "query_analytics")]
    public async Task Every_tool_of_a_server_kcap_auto_approves_is_allowed_in_auto_mode(string server, string tool) =>
        await Assert.That(await DecisionForPluginTool(server, tool)).IsEqualTo("allow");

    [Test]
    [Arguments("kcap-knowledge", "list_skills")]
    [Arguments("kcap-memory", "search_memories")]
    [Arguments("kcap-pages", "get_page")]
    [Arguments("kcap-flows", "list_flow_definitions")]
    public async Task A_read_on_any_other_kcap_server_is_allowed_in_auto_mode(string server, string tool) =>
        await Assert.That(await DecisionForPluginTool(server, tool)).IsEqualTo("allow");

    [Test]
    [Arguments("kcap-pages", "publish_page")]
    [Arguments("kcap-memory", "save_memory")]
    [Arguments("kcap-knowledge", "curate_fact_cluster")]
    [Arguments("kcap-flows", "start_flow")]
    public async Task A_write_on_any_other_kcap_server_stays_with_the_classifier(string server, string tool) =>
        await Assert.That(await DecisionForPluginTool(server, tool)).IsNull();

    /// <summary>A <c>.mcp.json</c> server named <c>plugin_kcap_kcap-workitems</c> gets this very tool
    /// name, so only Claude's report of where the server came from tells the two apart.</summary>
    [Test]
    [Arguments("plugin_kcap_kcap-workitems", "project")]
    [Arguments("plugin_kcap_kcap-workitems", "user")]
    [Arguments("plugin_kcap_kcap-workitems", "plugin")]
    [Arguments("plugin:kcap:kcap-workitems", "project")]
    [Arguments("plugin:kcap:kcap-workitems", null)]
    public async Task A_server_that_only_looks_like_the_plugin_stays_with_the_classifier(string name, string? source) =>
        await Assert.That(await DecisionFor(Merge, Server(name, source))).IsNull();

    [Test]
    public async Task A_call_with_no_server_report_stays_with_the_classifier() =>
        await Assert.That(await DecisionFor(Merge, mcpServer: null)).IsNull();

    [Test]
    public async Task A_wrong_typed_server_report_stays_with_the_classifier() =>
        await Assert.That(await DecisionFor(Merge, JsonValue.Create("plugin"))).IsNull();

    [Test]
    [Arguments("mcp__plugin_other_kcap-workitems__merge_work_item", "plugin:other:kcap-workitems")]
    [Arguments("mcp__plugin_kcap_kcap-unknown__read", "plugin:kcap:kcap-unknown")]
    [Arguments("mcp__plugin_linear_linear__save_issue", "plugin:linear:linear")]
    public async Task Another_plugins_tool_is_left_alone(string tool, string server) =>
        await Assert.That(await DecisionFor(tool, Server(server, "plugin"))).IsNull();

    [Test]
    [Arguments("default")]
    [Arguments("acceptEdits")]
    [Arguments("plan")]
    [Arguments("dontAsk")]
    [Arguments("bypassPermissions")]
    [Arguments(null)]
    public async Task Only_auto_mode_is_trusted(string? mode) =>
        await Assert.That(await DecisionFor(Merge, WorkItemsPlugin, mode)).IsNull();

    /// <summary>A rendered session runs tighten-only, so rule allows do not reach it. Trust has to,
    /// or a hosted auto-mode session stays blocked by the classifier.</summary>
    [Test]
    public async Task A_rendered_session_is_trusted_too() =>
        await Assert.That(await DecisionFor(Merge, WorkItemsPlugin, rendered: true)).IsEqualTo("allow");

    [Test]
    [Arguments("deny", false)]
    [Arguments("deny", true)]
    [Arguments("ask", false)]
    [Arguments("ask", true)]
    public async Task A_policy_rule_outranks_trust(string outcome, bool rendered) {
        WriteUserPolicy($"version: 1\nrules:\n  - match: {{ kind: mcp_tool, server: plugin_kcap_kcap-workitems, tool: merge_work_item }}\n    outcome: {outcome}\n");
        await Assert.That(await DecisionFor(Merge, WorkItemsPlugin, rendered: rendered)).IsEqualTo(outcome);
    }

    [Test]
    public async Task A_governed_session_is_trusted_where_no_rule_decides() {
        WriteUserPolicy("version: 1\nrules:\n  - match: { kind: shell, command: \"git push --force*\" }\n    outcome: deny\n");
        await Assert.That(await DecisionFor(Merge, WorkItemsPlugin)).IsEqualTo("allow");
        await Assert.That(SpooledPolicyEvents.Decisions(Config.Root, Sid).Count).IsEqualTo(0);
    }
}
