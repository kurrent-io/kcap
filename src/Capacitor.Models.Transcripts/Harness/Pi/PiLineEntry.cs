using Kurrent.Agent.Schema;

namespace Capacitor.Models.Transcripts.Harness.Pi;

/// What every event projected from one Pi entry shares.
sealed record PiLineEntry(
        string                               Line,
        string?                              SessionId,
        string?                              EntryId,
        string?                              ParentId,
        DateTimeOffset                       At,
        string?                              RecordTimestamp,
        IReadOnlyDictionary<string, string>? TreeNode
    ) {
    public Guid Id(string kind) =>
        SessionId is { Length: > 0 } sessionId && EntryId is { Length: > 0 } entryId
            ? TranscriptIds.PiEntry(sessionId, entryId, kind)
            : TranscriptIds.PiLine(Line, kind);

    /// <paramref name="ticks"/> past the entry's own instant, so the events of one entry keep their order wherever
    /// a reader sorts by time.
    public DateTimeOffset AtTicks(int ticks) => At.AddTicks(ticks);

    public CanonicalEvent Event(object payload, Guid id, int ticks = 0, TokenUsage? usage = null, long? cacheCreation = null) =>
        new(CanonicalEventTypes.Of(payload), payload, id, AtTicks(ticks), RecordTimestamp, Usage: usage, CacheCreationTokens: cacheCreation) {
            VendorMetadata = TreeNode,
        };
}
