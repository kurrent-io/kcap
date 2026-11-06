using System.Text.Json;

namespace Capacitor.Cli.Core;

public static class JsonRecord {
    /// True if <paramref name="line"/>, a single newline-less transcript line, parses as a complete JSON document.
    public static bool IsComplete(string line) {
        var trimmed = line.Trim();
        if (trimmed.Length == 0) return false;

        try {
            using var _ = JsonDocument.Parse(trimmed);
            return true;
        } catch (JsonException) {
            return false;
        }
    }
}
