using System.Text;
using Capacitor.Cli.Core.Harness.Claude;
using Capacitor.Cli.Core.Harness.Headless;

namespace Capacitor.Cli.Core.Harness.OpenCode;

/// <summary>
/// Reads OpenCode's <c>--print-logs</c> stderr for a provider spend limit. On one, OpenCode logs a
/// <c>stream error</c> and retries without emitting any JSON event until it is killed, so this line is the only
/// sign of it.
/// </summary>
static class OpenCodeLimitLog {
    /// <summary>The <c>error.error</c> message of an ERROR-level <c>stream error</c> line that
    /// <see cref="HeadlessFailureClassifier"/> calls <see cref="ClaudeCliFailure.SpendBudget"/>, else null.</summary>
    public static string? Match(string stderrLine) {
        var fields = Fields(stderrLine);

        if (fields.GetValueOrDefault("level") != "ERROR" || fields.GetValueOrDefault("message") != "stream error") return null;

        return fields.GetValueOrDefault("error.error") is { } message
            && HeadlessFailureClassifier.Classify(HeadlessFailureClassifier.StatusFromMessage(message), message) == ClaudeCliFailure.SpendBudget
            ? message
            : null;
    }

    /// <summary>The line's <c>key=value</c> and <c>key="quoted \" value"</c> pairs, read in order so text inside a
    /// quoted value is never taken for a key. The first occurrence of a key wins.</summary>
    static Dictionary<string, string> Fields(string line) {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        var i      = 0;

        while (i < line.Length) {
            while (i < line.Length && line[i] == ' ') i++;

            var keyStart = i;
            while (i < line.Length && line[i] is not ('=' or ' ')) i++;

            var key = line[keyStart..i];

            if (i >= line.Length || line[i] != '=') continue;

            i++;
            var value = new StringBuilder();

            if (i < line.Length && line[i] == '"') {
                for (i++; i < line.Length && line[i] != '"'; i++) {
                    if (line[i] == '\\' && i + 1 < line.Length) i++;
                    value.Append(line[i]);
                }

                i++;
            } else {
                while (i < line.Length && line[i] != ' ') value.Append(line[i++]);
            }

            fields.TryAdd(key, value.ToString());
        }

        return fields;
    }
}
