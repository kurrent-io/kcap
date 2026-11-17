using Google.Protobuf.WellKnownTypes;

namespace Capacitor.Models.Transcripts;

/// The `acp` extension slug: what an Agent Client Protocol update says that no schema field holds.
public static class AcpExtension {
    public const string Slug = "acp";

    /// On a tool result whose call failed. An ACP update's status is the only failure signal it gives: there
    /// is no exit code and no vendor flag to read instead.
    public static Struct ToolResultError() => new() { Fields = { ["is_error"] = Value.ForBool(true) } };
}
