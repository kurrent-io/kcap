namespace Capacitor.Models.Transcripts.Harness.Gemini;

/// What a Gemini tool call did, in the vendor-neutral vocabulary, from Gemini's own tool name. The
/// built-in tools name their kind; subagents, `ask_user`, memory, todos and MCP tools are `other`.
public static class GeminiToolKinds {
    public static string Of(string? toolName) => toolName switch {
        "read_file" or "read_many_files"              => AcpToolKind.Read,
        "write_file" or "replace"                     => AcpToolKind.Edit,
        "run_shell_command"                           => AcpToolKind.Execute,
        "list_directory" or "glob"
            or "grep_search" or "search_file_content" => AcpToolKind.Search,
        "web_fetch" or "google_web_search"            => AcpToolKind.Fetch,
        _                                             => AcpToolKind.Other,
    };
}
