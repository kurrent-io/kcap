using Capacitor.Models.Transcripts.Harness.Claude;
using Capacitor.Models.Transcripts.Harness.OpenCode;

namespace Capacitor.Models.Transcripts;

public static class TranscriptSignals {
    public static ILineSignalReader? For(string vendor) => vendor switch {
        "claude"   => ClaudeLineSignals.Instance,
        "opencode" => OpenCodeLineSignals.Instance,
        _          => null,
    };
}
