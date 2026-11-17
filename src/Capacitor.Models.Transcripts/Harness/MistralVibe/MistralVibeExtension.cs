using Google.Protobuf.WellKnownTypes;

namespace Capacitor.Models.Transcripts.Harness.MistralVibe;

/// The `mistral_vibe` extension slug. On a `subagent.spawn` call it names the child the call
/// started, so the child's sub-session nests under it.
public static class MistralVibeExtension {
    public const string Slug    = "mistral_vibe";
    public const string AgentId = "agent_id";

    const string ChildPrefix = "child-";

    /// The id a child store (`child-&lt;hex&gt;`, a sibling of the parent's) is recorded under: its hex
    /// suffix, since agent ids carry no hyphen. Null for a name that is not a child store.
    public static string? AgentIdOf(string? childSessionId) =>
        childSessionId is not null
     && childSessionId.StartsWith(ChildPrefix, StringComparison.Ordinal)
     && childSessionId[ChildPrefix.Length..] is { Length: > 0 } suffix
     && suffix.All(char.IsAsciiLetterOrDigit)
            ? suffix.ToLowerInvariant()
            : null;

    public static Struct ToolCall(string agentId) {
        var block = new Struct();
        block.Fields[AgentId] = Value.ForString(agentId);
        return block;
    }
}
