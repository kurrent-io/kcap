namespace Capacitor.Models.Transcripts.Harness.OpenCode;

/// What an OpenCode tool call did, in the vendor-neutral vocabulary. Built-in tools carry their kind in
/// their id. `codesearch` queries code on the web, not the workspace, so it is a fetch. An MCP tool
/// arrives as `server_tool` and lands on `other` with subagents, skills and todos.
public static class OpenCodeToolKinds {
    public static string Of(string? toolName) => toolName switch {
        "read"                                                       => AcpToolKind.Read,
        "edit" or "multiedit" or "write" or "patch" or "apply_patch" => AcpToolKind.Edit,
        "bash"                                                       => AcpToolKind.Execute,
        "grep" or "glob" or "list"                                   => AcpToolKind.Search,
        "webfetch" or "websearch" or "codesearch"                    => AcpToolKind.Fetch,
        _                                                            => AcpToolKind.Other,
    };
}
