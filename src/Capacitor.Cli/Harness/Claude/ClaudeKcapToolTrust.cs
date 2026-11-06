namespace Capacitor.Cli.Harness.Claude;

using Capacitor.Cli.Commands;
using Capacitor.Cli.Core.Harness.Claude;
using Capacitor.Cli.Core.Mcp;
using Capacitor.Cli.Core.Policy;

/// <summary>
/// The kcap tools the PreToolUse hook answers for in Claude's auto mode. A plugin cannot ship
/// permission rules and the classifier ignores MCP annotations, so a hook <c>allow</c> is what keeps
/// it from refusing kcap's own bookkeeping as a write to an external system. Every other mode keeps
/// the user's prompts. Only the plugin-scoped name counts: a bare <c>mcp__kcap-*</c> is a name a
/// repository's <c>.mcp.json</c> can claim.
/// </summary>
internal static class ClaudeKcapToolTrust {
    internal const string Reason = "kcap's own tool";

    const string AutoMode     = "auto";
    const string PluginPrefix = "plugin_kcap_";

    public static bool Covers(string? toolName, string? permissionMode) {
        if (permissionMode != AutoMode) return false;

        var action = ClaudeActionNormalizer.Normalize(toolName, null, null);
        if (action.Kind != ActionKind.McpTool || action.Server?.StartsWith(PluginPrefix, StringComparison.Ordinal) != true)
            return false;

        var name = action.Server[PluginPrefix.Length..];

        return KcapMcpServers.All.FirstOrDefault(s => s.Name == name) is { } server
            && (server.AutoApprove || ToolsOf(name).Any(t => t.Name == action.Tool && t.Annotations.ReadOnlyHint == true));
    }

    static McpTool[] ToolsOf(string server) => server switch {
        "kcap-flows"     => McpFlowsServer.BuildToolsList(),
        "kcap-memory"    => McpMemoryServer.BuildToolsList(),
        "kcap-pages"     => McpArtefactsServer.BuildToolsList(),
        "kcap-knowledge" => McpKnowledgeServer.BuildToolsList(),
        _                => [],
    };
}
