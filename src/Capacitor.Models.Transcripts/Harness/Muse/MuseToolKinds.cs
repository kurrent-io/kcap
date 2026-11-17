namespace Capacitor.Models.Transcripts.Harness.Muse;

/// What a Muse tool call did, in the vendor-neutral vocabulary. Muse's built-in tools name their kind;
/// `subagent_spawn`, `request_user_input`, `write_todos` and MCP tools are `other`.
public static class MuseToolKinds {
    public static string Of(string toolName) => toolName switch {
        "read_file"                 => AcpToolKind.Read,
        "write_file" or "edit_file" => AcpToolKind.Edit,
        "bash"                      => AcpToolKind.Execute,
        "search"                    => AcpToolKind.Search,
        "web_fetch" or "web_search" => AcpToolKind.Fetch,
        _                           => AcpToolKind.Other,
    };
}
