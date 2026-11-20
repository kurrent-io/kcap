namespace Capacitor.Models.Transcripts.Harness.Pi;

/// What a Pi tool call did, in the vendor-neutral vocabulary. Pi's built-in tools carry their kind in their
/// name; an extension tool such as <c>subagent</c>, or an MCP tool, is <see cref="AcpToolKind.Other"/>.
public static class PiToolKinds {
    public static string Of(string? toolName) => toolName switch {
        "read"                      => AcpToolKind.Read,
        "edit" or "write"           => AcpToolKind.Edit,
        "bash" or "powershell"      => AcpToolKind.Execute,
        "grep" or "find" or "ls"    => AcpToolKind.Search,
        _                           => AcpToolKind.Other,
    };
}
