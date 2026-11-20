using Google.Protobuf.WellKnownTypes;

namespace Capacitor.Models.Transcripts.Harness.Kiro;

/// The `kiro` extension slug, shaped as the server's records serialize: every field present, null when
/// Kiro gave none. The server's read side unpacks these blocks into those records.
public static class KiroExtension {
    public const string Slug = "kiro";

    /// On a tool result. `tool_name` stays null: the server's record declares it and nothing fills it.
    public static Struct ToolResult(string status) {
        var block = new Struct();
        block.Fields["success"] = status switch {
            "success" => Value.ForBool(true),
            "error"   => Value.ForBool(false),
            _         => Value.ForNull(),
        };
        block.Fields["tool_name"] = Value.ForNull();
        block.Fields["status"]    = Value.ForString(status);
        return block;
    }

    /// On a turn's first event: the credits it billed and its end-of-turn context fill, in percent.
    public static Struct Usage(double? credits, double? contextUsagePercentage) {
        var block = new Struct();
        block.Fields["credits"]                  = credits is { } c ? Value.ForNumber(c) : Value.ForNull();
        block.Fields["context_usage_percentage"] = contextUsagePercentage is { } p ? Value.ForNumber(p) : Value.ForNull();
        return block;
    }
}
