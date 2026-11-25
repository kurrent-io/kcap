using Google.Protobuf.WellKnownTypes;

namespace Capacitor.Models.Transcripts.Harness.Antigravity;

/// The `antigravity` extension slug, shaped as the server's records serialize. The server's read side counts a
/// tool result carrying this slug as a failure.
public static class AntigravityExtension {
    public const string Slug       = "antigravity";
    public const string IsError    = "is_error";
    public const string NativeName = "native_name";

    public static Struct ToolResultFailed() {
        var block = new Struct();
        block.Fields[IsError] = Value.ForBool(true);
        return block;
    }

    /// The tool's own name, on a call the projection translated and on an interrupt it made from an interaction tool.
    public static Struct Native(string nativeName) {
        var block = new Struct();
        block.Fields[NativeName] = Value.ForString(nativeName);
        return block;
    }
}
