namespace Capacitor.Models.Transcripts.Harness.Cursor;

/// What a Cursor tool call did, in the vendor-neutral vocabulary. Cursor's built-in tools carry their kind in
/// their name; subagents (`Task`), todos and MCP tools are `other`.
public static class CursorToolKinds {
    public static string Of(string toolName) => toolName switch {
        "Read"                                    => AcpToolKind.Read,
        "Write" or "StrReplace" or "EditNotebook" => AcpToolKind.Edit,
        "Delete"                                  => AcpToolKind.Delete,
        "Shell"                                   => AcpToolKind.Execute,
        "Glob" or "Grep" or "LS"                  => AcpToolKind.Search,
        "WebSearch" or "WebFetch"                 => AcpToolKind.Fetch,
        _                                         => AcpToolKind.Other,
    };
}
