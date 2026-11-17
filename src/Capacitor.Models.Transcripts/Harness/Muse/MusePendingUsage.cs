namespace Capacitor.Models.Transcripts.Harness.Muse;

/// A Muse <c>model_completed</c> report waiting for the next assistant event of its run. <see cref="Input"/>
/// excludes the cached tokens, which <see cref="CacheRead"/> carries. <see cref="ReportIds"/> names the records
/// folded in, so a report the watcher re-delivers is not counted twice.
public sealed record MusePendingUsage(long Input, long Output, long CacheRead, long Reasoning, string? Model, IReadOnlySet<string> ReportIds);
