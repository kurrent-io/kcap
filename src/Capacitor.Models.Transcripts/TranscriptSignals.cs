using Capacitor.Models.Transcripts.Harness.Claude;

namespace Capacitor.Models.Transcripts;

public static class TranscriptSignals {
    public static ILineSignalReader? For(string vendor) => vendor == "claude" ? ClaudeLineSignals.Instance : null;
}
