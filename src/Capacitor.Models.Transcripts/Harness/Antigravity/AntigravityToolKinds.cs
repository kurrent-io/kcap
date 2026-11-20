namespace Capacitor.Models.Transcripts.Harness.Antigravity;

/// What an Antigravity tool call did, in the vendor-neutral vocabulary, from Antigravity's own tool name. A
/// browser, subagent or MCP tool, and any name not listed, is `other`.
public static class AntigravityToolKinds {
    public static string Of(string toolName) => toolName switch {
        "view_file" or "view_file_outline" or "view_code_item"                 => AcpToolKind.Read,
        "write_to_file" or "replace_file_content" or "multi_replace_file_content" => AcpToolKind.Edit,
        "run_command"                                                          => AcpToolKind.Execute,
        "list_dir" or "grep_search" or "find_by_name" or "codebase_search"     => AcpToolKind.Search,
        "search_web" or "read_url_content"                                     => AcpToolKind.Fetch,
        _                                                                      => AcpToolKind.Other,
    };
}
