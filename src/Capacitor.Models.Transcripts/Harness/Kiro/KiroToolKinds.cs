namespace Capacitor.Models.Transcripts.Harness.Kiro;

/// What a Kiro tool call did, in the vendor-neutral vocabulary. Kiro's native tools carry their kind in
/// their name; an MCP tool, `use_aws` and the rest are `other`.
public static class KiroToolKinds {
    public static string Of(string toolName) => toolName switch {
        "fs_read"                   => AcpToolKind.Read,
        "fs_write"                  => AcpToolKind.Edit,
        "execute_bash"              => AcpToolKind.Execute,
        "glob" or "grep"            => AcpToolKind.Search,
        "web_search" or "web_fetch" => AcpToolKind.Fetch,
        "thinking"                  => AcpToolKind.Think,
        _                           => AcpToolKind.Other,
    };
}
