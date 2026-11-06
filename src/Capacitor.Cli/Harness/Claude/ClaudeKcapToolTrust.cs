namespace Capacitor.Cli.Harness.Claude;

using Capacitor.Cli.Commands;
using Capacitor.Cli.Core.Harness.Claude;
using Capacitor.Cli.Core.Mcp;
using Capacitor.Cli.Core.Policy;

/// <summary>
/// The kcap tools the PreToolUse hook answers for in Claude's auto mode. A plugin cannot ship
/// permission rules and the classifier ignores MCP annotations, so a hook <c>allow</c> is what keeps
/// it from refusing kcap's own bookkeeping as a write to an external system. Every other mode keeps
/// the user's prompts. Trust rests on where Claude says the server came from, never on the tool
/// name: a repository's <c>.mcp.json</c> can name a server so its tools carry the plugin's exact
/// prefix. A call with no such report stays with the classifier.
/// </summary>
internal static class ClaudeKcapToolTrust {
    internal const string Reason = "kcap's own tool";

    const string AutoMode     = "auto";
    const string PluginSource = "plugin";
    const string PluginPrefix = "plugin:kcap:";

    public static bool Covers(string? toolName, string? mcpServerName, string? mcpServerSource, string? permissionMode) {
        if (permissionMode != AutoMode || mcpServerSource != PluginSource) return false;
        if (mcpServerName?.StartsWith(PluginPrefix, StringComparison.Ordinal) != true) return false;

        var name   = mcpServerName[PluginPrefix.Length..];
        var action = ClaudeActionNormalizer.Normalize(toolName, null, null);

        return action.Kind == ActionKind.McpTool
            && KcapMcpServers.All.FirstOrDefault(s => s.Name == name) is { } server
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
