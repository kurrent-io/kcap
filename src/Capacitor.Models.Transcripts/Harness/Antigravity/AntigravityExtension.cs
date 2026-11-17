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

    /// On an interrupt the projection made from one of Antigravity's interaction tools: the tool's own name.
    public static Struct Interaction(string nativeName) {
        var block = new Struct();
        block.Fields[NativeName] = Value.ForString(nativeName);
        return block;
    }
}
