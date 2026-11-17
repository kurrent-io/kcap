using System.Text.Json;
using System.Text.RegularExpressions;
using Capacitor.Cli.Core.RepoEvidence;

namespace Capacitor.Cli.Core.Harness.Gemini;

/// <summary>
/// The PRs a Gemini session creates. A <c>gemini</c> record's <c>toolCalls[]</c> carries each
/// <c>run_shell_command</c> whole: its <c>args.command</c>, its <c>status</c>, and its output under
/// <c>result[].functionResponse.response.output</c>. Gemini writes a tool turn twice under one id, which the
/// recorder's per-PR dedupe absorbs.
/// </summary>
public sealed partial class GeminiCreatedPullRequestAdapter : ICreatedPullRequestAdapter {
    public static GeminiCreatedPullRequestAdapter Instance { get; } = new();

    GeminiCreatedPullRequestAdapter() { }

    public void Observe(string line, CreatedPullRequestEvidence evidence) {
        if (!line.Contains("\"run_shell_command\"", StringComparison.Ordinal)) return;

        try {
            using var doc = JsonDocument.Parse(line);

            if (doc.RootElement.Str("type") != "gemini" || doc.RootElement.Arr("toolCalls") is not { } calls) return;

            foreach (var call in calls.EnumerateArray()) {
                if (call.Str("name") != "run_shell_command" || call.Obj("args")?.Str("command") is not { } command) continue;
                if (Output(call.Arr("result")) is not { } output) continue;

                var (text, failed) = Parse(output);
                evidence.Completed(call.Str("id"), command, text, succeeded: call.Str("status") == "success" && !failed);
            }
        } catch (JsonException) {
            // fail-open: an unreadable line links nothing
        }
    }

    static string? Output(JsonElement? result) {
        if (result is not { } r) return null;

        foreach (var item in r.EnumerateArray()) {
            if (item.Obj("functionResponse")?.Obj("response")?.Str("output") is { } output) return output;
        }

        return null;
    }

    /// <summary>The command's own output out of the tool's report, <c>Output: …</c> (or <c>Stdout: …</c>) followed by
    /// labelled lines. Gemini adds <c>Exit Code: N</c> only for a non-zero exit, and an <c>Error</c> or <c>Signal</c>
    /// other than <c>(none)</c> when the command did not run to completion.</summary>
    static (string Text, bool Failed) Parse(string report) {
        var lines  = report.Split('\n').ToList();
        var failed      = false;
        var hasExitCode = false;

        while (lines.Count > 0) {
            if (string.IsNullOrWhiteSpace(lines[^1])) {
                lines.RemoveAt(lines.Count - 1);
                continue;
            }

            if (Trailer().Match(lines[^1]) is not { Success: true } trailer) break;

            var value = trailer.Groups["value"].Value.Trim();
            hasExitCode |= trailer.Groups["label"].Value == "Exit Code";

            failed |= trailer.Groups["label"].Value switch {
                "Exit Code"         => value != "0",
                "Error" or "Signal" => value is not ("" or "(none)"),
                _                   => false,
            };

            lines.RemoveAt(lines.Count - 1);
        }

        while (lines.Count > 0 && (lines[0].StartsWith("Command: ", StringComparison.Ordinal) || lines[0].StartsWith("Directory: ", StringComparison.Ordinal))) lines.RemoveAt(0);
        // A cancelled or backgrounded command is reported in prose, with no labelled output to read.
        if (lines.Count == 0 || Lead().Match(lines[0]) is not { Success: true } lead) return ("", true);

        // The older layout states every exit code, 0 included; only the newer one leaves 0 unsaid.
        if (lead.Value.StartsWith("Stdout", StringComparison.Ordinal) && !hasExitCode) failed = true;

        lines[0] = lines[0][lead.Length..];

        return (string.Join('\n', lines), failed);
    }

    [GeneratedRegex(@"^(?<label>Error|Exit Code|Signal|Background PIDs|Process Group PGID|Stderr): ?(?<value>.*)$")]
    private static partial Regex Trailer();

    [GeneratedRegex(@"^(Output|Stdout): ?")]
    private static partial Regex Lead();
}
