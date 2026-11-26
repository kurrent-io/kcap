using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.Transcripts;

/// <summary>Who sent the message a user message event came from: the dispatch it carried with the agent it was sent to,
/// whose input stream the server reads it from, or the session's owner at a local surface</summary>
public sealed record CanonicalBatchStamp(
        [property: JsonPropertyName("dispatch_id"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? DispatchId,
        [property: JsonPropertyName("agent_id"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? AgentId,
        [property: JsonPropertyName("local"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Local
    ) {
    /// <summary>The wire form of <paramref name="stamp"/>, or null when it names both senders, neither, the empty
    /// dispatch, or a dispatch without its agent: the server drops such a stamp and stores the row unstamped</summary>
    public static CanonicalBatchStamp? Of(LineStamp stamp) => stamp switch {
        { DispatchId: { } id, AgentId: { Length: > 0 } agent, IsLocal: false } when id != Guid.Empty => new(id, agent, Local: false),
        { DispatchId: null, AgentId: null, IsLocal: true }                                         => new(null, null, Local: true),
        _                                                                                          => null,
    };
}
