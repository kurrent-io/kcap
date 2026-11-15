using Google.Protobuf.WellKnownTypes;

namespace Capacitor.Models.Transcripts.Harness.Copilot;

/// The `copilot` extension slug, shaped as the server's records serialize: every field of a block present, null when
/// Copilot gave none. The server's read side unpacks these blocks into those records.
public static class CopilotExtension {
    public const string Slug = "copilot";

    /// On SessionStarted: the transcript's own context, or only <paramref name="source"/> on a fork's link.
    public static Struct Session(
            string? cwd            = null,
            string? gitRoot        = null,
            string? branch         = null,
            string? repository     = null,
            string? repositoryHost = null,
            string? hostType       = null,
            string? copilotVersion = null,
            string? producer       = null,
            string? source         = null
        ) {
        var block = new Struct();
        block.Fields["cwd"]             = Text(cwd);
        block.Fields["git_root"]        = Text(gitRoot);
        block.Fields["branch"]          = Text(branch);
        block.Fields["repository"]      = Text(repository);
        block.Fields["repository_host"] = Text(repositoryHost);
        block.Fields["host_type"]       = Text(hostType);
        block.Fields["copilot_version"] = Text(copilotVersion);
        block.Fields["producer"]        = Text(producer);
        block.Fields["source"]          = Text(source);
        return block;
    }

    /// On a subagent's prompt, reply, thinking or tool call: Copilot inlines a subagent's lines in its parent's
    /// transcript, tagged with this id.
    public static Struct Turn(string agentId) {
        var block = new Struct();
        block.Fields["agent_id"] = Value.ForString(agentId);
        return block;
    }

    /// On a tool result: the outcome flag, the human-facing detail when it differs from the result, and the
    /// subagent that ran the tool.
    public static Struct ToolResult(bool? success, string? detailedContent, string? agentId) {
        var block = new Struct();
        block.Fields["success"]          = success is { } s ? Value.ForBool(s) : Value.ForNull();
        block.Fields["detailed_content"] = Text(detailedContent);
        block.Fields["agent_id"]         = Text(agentId);
        return block;
    }

    static Value Text(string? text) => text is null ? Value.ForNull() : Value.ForString(text);
}
