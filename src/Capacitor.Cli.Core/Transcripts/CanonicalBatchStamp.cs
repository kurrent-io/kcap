using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.Transcripts;

/// <summary>Who sent the message a user message event came from: the dispatch it carried, or the session's owner at a
/// local surface. Exactly one field is set</summary>
public sealed record CanonicalBatchStamp(
        [property: JsonPropertyName("dispatch_id"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? DispatchId,
        [property: JsonPropertyName("local"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Local
    ) {
    /// <summary>The wire form of <paramref name="stamp"/>, or null when it names both senders, neither, or the empty
    /// dispatch: the server refuses the whole batch for such a stamp</summary>
    public static CanonicalBatchStamp? Of(LineStamp stamp) => stamp switch {
        { DispatchId: { } id, IsLocal: false } when id != Guid.Empty => new(id, Local: false),
        { DispatchId: null, IsLocal: true }                         => new(null, Local: true),
        _                                                           => null,
    };
}
