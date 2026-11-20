namespace Capacitor.Models.Transcripts.Harness.Grok;

/// What a Grok tool call did, from the kind Grok puts on the tool: ACP's own tokens, apart from two Grok
/// names for things ACP calls otherwise. Anything else Grok sends is `other`; no kind stays null.
public static class GrokToolKinds {
    public static string? Of(string? grokKind) => AcpToolKind.Normalize(grokKind switch {
        "list"  => AcpToolKind.Read,
        "write" => AcpToolKind.Edit,
        _       => grokKind,
    });
}
