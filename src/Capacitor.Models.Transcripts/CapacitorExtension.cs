namespace Capacitor.Models.Transcripts;

/// The `capacitor` extension slug, which only the kcap server writes.
public static class CapacitorExtension {
    public const string Slug = "capacitor";
    /// On a user message the server wrote from a queued input the agent never picked up.
    public const string Undelivered = "undelivered";
}
