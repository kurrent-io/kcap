namespace Capacitor.Cli.Core.Transcripts;

/// kcap's own stand-in for a transcript line it could not capture. The recorder writes `type` as the first
/// property, so a line is one by its prefix, without parsing a line that may be megabytes long.
public static class CaptureLossLine {
    public const string Type = "kcap_capture_loss";

    const string Prefix = "{\"type\":\"" + Type + "\"";

    public static bool Is(string line) => line.StartsWith(Prefix, StringComparison.Ordinal);
}
