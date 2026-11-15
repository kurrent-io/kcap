namespace Capacitor.Models.Transcripts.Harness.Copilot;

/// What a Copilot tool call did, in the vendor-neutral vocabulary. Copilot CLI and VS Code's harnesses name their
/// built-in tools differently, so both sets are listed. A shell call stays `execute` however its command reads: both
/// have real read and search tools. Subagents, intents, todos and MCP tools are `other`.
public static class CopilotToolKinds {
    public static string Of(string toolName) => toolName switch {
        "view" or "read_file" => AcpToolKind.Read,
        "create" or "edit" or "apply_patch" or "create_file" or "replace_string_in_file"
            or "multi_replace_string_in_file" or "insert_edit_into_file" => AcpToolKind.Edit,
        "bash" or "read_bash" or "write_bash" or "stop_bash" or "powershell" or "read_powershell"
            or "write_powershell" or "stop_powershell" or "run_in_terminal" or "get_terminal_output" => AcpToolKind.Execute,
        "glob" or "grep" or "file_search" or "grep_search" or "list_dir" => AcpToolKind.Search,
        "web_fetch" or "web_search" or "fetch_webpage" => AcpToolKind.Fetch,
        _ => AcpToolKind.Other,
    };
}
