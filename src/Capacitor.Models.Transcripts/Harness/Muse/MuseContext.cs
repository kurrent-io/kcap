namespace Capacitor.Models.Transcripts.Harness.Muse;

/// Per-stream state, all of it spanning batches and none of it depending on what the caller stored. A caller that
/// starts projecting a stream partway through a run seeds both, or that run's reported usage is never counted.
public sealed class MuseContext : TranscriptContext {
    /// Usage each run reported and no assistant event of that run has taken yet, keyed by run id.
    public Dictionary<string, MusePendingUsage> PendingUsage { get; } = new(StringComparer.Ordinal);

    /// The model the latest configuration record named, for a usage report that names none.
    public string? CurrentModel { get; set; }
}
