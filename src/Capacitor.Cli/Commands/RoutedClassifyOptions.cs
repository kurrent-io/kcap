using Capacitor.Cli.Core.Harness;

namespace Capacitor.Cli.Commands;

/// <summary>What a routed source tells <see cref="RoutedImportClassifier"/> about its transcripts.</summary>
/// <param name="IsImportRelevantLine">True for a line the server's normalizer turns into an event; only those advance the watermark.</param>
/// <param name="EndedAt">The session's end time, read from the transcript once the probe succeeded.</param>
internal sealed record RoutedClassifyOptions(
    HarnessId                      Vendor,
    TimeProvider                   Time,
    Func<string, bool>             IsImportRelevantLine,
    Func<string, DateTimeOffset?>  EndedAt) {
    /// <summary>An end time the source recorded at discovery. It wins over <see cref="EndedAt"/>, which then is not read.</summary>
    public Func<DiscoveredSession, DateTimeOffset?>? RecordedEndedAt { get; init; }

    /// <summary>
    /// Settles a session the server holds lines for, given the server's last line and the transcript's last
    /// relevant one. Null compares the two directly.
    /// </summary>
    public Func<DiscoveredSession, int, int, WatermarkResolution>? ResolveWatermark { get; init; }

    /// <summary>When false, a cancelled watermark probe classifies the session ProbeError like any other probe failure.</summary>
    public bool RethrowProbeCancellation { get; init; }
}
