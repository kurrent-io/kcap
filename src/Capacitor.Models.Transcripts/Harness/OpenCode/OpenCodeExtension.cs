using Google.Protobuf.WellKnownTypes;

namespace Capacitor.Models.Transcripts.Harness.OpenCode;

/// The `opencode` extension slug, under the field names the server writes and reads.
public static class OpenCodeExtension {
    public const string Slug    = "opencode";
    public const string AgentId = "agent_id";
    public const string Success = "success";
    public const string Status  = "status";
    public const string Title   = "title";

    /// On a `task` call: the child session it spawned, whose transcript is recorded under that agent id.
    public static Struct ToolCall(string childSessionId) {
        var block = new Struct();
        block.Fields[AgentId] = Value.ForString(Guid.TryParse(childSessionId, out var guid) ? guid.ToString("N") : childSessionId);
        return block;
    }

    /// On a settled tool result. The title is written as null when the part has none, as the server writes it.
    public static Struct ToolResult(string status, string? title) {
        var block = new Struct();
        block.Fields[Success] = Value.ForBool(status == "completed");
        block.Fields[Status]  = Value.ForString(status);
        block.Fields[Title]   = title is null ? Value.ForNull() : Value.ForString(title);
        return block;
    }
}
