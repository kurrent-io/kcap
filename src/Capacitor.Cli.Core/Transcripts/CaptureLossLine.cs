using System.Text.Json;

namespace Capacitor.Cli.Core.Transcripts;

/// kcap's own stand-in for a transcript line it could not capture: a JSON object whose `type` is one of these, as the
/// server's capture-loss parser reads it. The two `redacted_` forms are what older CLIs wrote, and a spool tail they
/// left can still hold them.
public static class CaptureLossLine {
    public const string Type = "kcap_capture_loss";

    public const string OversizeType = "redacted_oversize_line";

    public const string UnparsableType = "redacted_unparsable_line";

    public static bool Is(string line) {
        // Only a line naming a type is parsed: nearly every line is not one, and some are megabytes long.
        if (!line.Contains(Type, StringComparison.Ordinal) && !line.Contains(OversizeType, StringComparison.Ordinal)
         && !line.Contains(UnparsableType, StringComparison.Ordinal)) return false;

        try {
            using var document = JsonDocument.Parse(line);
            return document.RootElement.Str("type") is Type or OversizeType or UnparsableType;
        } catch (JsonException) {
            return false;
        }
    }
}
