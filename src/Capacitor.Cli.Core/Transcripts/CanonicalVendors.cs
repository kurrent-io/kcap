namespace Capacitor.Cli.Core.Transcripts;

/// <summary>The vendors whose transcript lines the client projects to canonical events. A batch with no vendor is
/// Claude's.</summary>
public static class CanonicalVendors {
    public static bool Contains(string? vendor) => vendor is null or "claude";
}
