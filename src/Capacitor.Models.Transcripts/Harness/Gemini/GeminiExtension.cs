using Google.Protobuf.WellKnownTypes;

namespace Capacitor.Models.Transcripts.Harness.Gemini;

/// The `gemini` extension slug: a tool call's display hints and, on an `invoke_agent` call, the
/// subagent it spawned; a tool result's status. Every field is written, nulls included, as the
/// server packs these blocks.
public static class GeminiExtension {
    public const string Slug        = "gemini";
    public const string DisplayName = "display_name";
    public const string Description = "description";
    public const string AgentId     = "agent_id";
    public const string Success     = "success";
    public const string Status      = "status";

    /// Null when the call carries none of the three.
    public static Struct? ToolCall(string? displayName, string? description, string? agentId) {
        if (displayName is null && description is null && agentId is null) return null;

        var block = new Struct();
        block.Fields[DisplayName] = Text(displayName);
        block.Fields[Description] = Text(description);
        block.Fields[AgentId]     = Text(agentId);
        return block;
    }

    /// Null when the call has no status; any status but `success` is a failure.
    public static Struct? ToolResult(string? status) {
        if (status is null) return null;

        var block = new Struct();
        block.Fields[Success] = Value.ForBool(status == "success");
        block.Fields[Status]  = Value.ForString(status);
        return block;
    }

    static Value Text(string? value) => value is null ? Value.ForNull() : Value.ForString(value);
}
