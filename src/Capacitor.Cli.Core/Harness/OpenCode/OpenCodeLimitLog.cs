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
        if (Value(stderrLine, "level") != "ERROR" || Value(stderrLine, "message") != "stream error") return null;

        return Value(stderrLine, "error.error") is { } message
            && HeadlessFailureClassifier.Classify(HeadlessFailureClassifier.StatusFromMessage(message), message) == ClaudeCliFailure.SpendBudget
            ? message
            : null;
    }

    /// <summary>The value of <c>key=value</c> or <c>key="quoted \" value"</c>, where the key starts the line or
    /// follows a space.</summary>
    static string? Value(string line, string key) {
        var token = key + "=";
        var at    = 0;

        while ((at = line.IndexOf(token, at, StringComparison.Ordinal)) >= 0) {
            if (at == 0 || line[at - 1] == ' ') break;

            at += token.Length;
        }

        if (at < 0) return null;

        var start = at + token.Length;

        if (start >= line.Length || line[start] != '"') {
            var end = line.IndexOf(' ', start);

            return end < 0 ? line[start..] : line[start..end];
        }

        var value = new StringBuilder();

        for (var i = start + 1; i < line.Length; i++) {
            switch (line[i]) {
                case '\\' when i + 1 < line.Length:
                    value.Append(line[++i]);

                    break;
                case '"':
                    return value.ToString();
                default:
                    value.Append(line[i]);

                    break;
            }
        }

        return null;
    }
}
