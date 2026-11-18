namespace Capacitor.Models.Transcripts;

/// <summary>What the server reads from a raw transcript line besides its canonical events: the record's ids, type
/// and timestamp as written, a harness title, and whether the line ends a turn.</summary>
public sealed record LineSignals(string? Uuid, string? ParentUuid, string? RecordType, string? Timestamp, TitleSighting? Title, bool EndsTurn) {
    public static readonly LineSignals None = new(null, null, null, null, null, false);
}
