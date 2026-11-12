using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness.Copilot;

namespace Capacitor.Cli.Recording.Harness.Copilot;

/// <summary>
/// A VS Code Local transcript carries no usage; the session's chat log does, written seconds after the transcript's
/// last line. Each drain turns what the log holds into the lines Copilot's own transcripts carry: one
/// <c>session.usage_record</c> per completed request (its output and the model it ran on) and a
/// <c>session.usage_checkpoint</c> with the session's credits so far. Each line's id is derived from what it reports,
/// so a live send and a re-import of the same figure dedupe.
/// </summary>
public static class CopilotLocalUsage {
    // Clear of real line numbers and of the Antigravity and Kiro bands; the server keeps lines this high out of its
    // transcript watermark.
    public const int LineBase = 1_500_000_000;

    public static CopilotLocalUsageLines Build(string sessionId, VsCodeChatSession session, IReadOnlySet<string> sentRequests, long sentNanoAiu) {
        var scope       = sessionId.Replace("-", "").ToLowerInvariant();
        var lines       = new List<string>();
        var lineNumbers = new List<int>();
        var requestIds  = new List<string>();
        var seen        = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < session.Requests.Count; i++) {
            var request = session.Requests[i];
            if (!request.Complete || request.CompletionTokens is not (> 0 and var tokens) || request.Model is not { } model) continue;
            if (sentRequests.Contains(request.RequestId) || !seen.Add(request.RequestId)) continue;

            lines.Add(UsageRecordLine(scope, request.RequestId, model, tokens));
            lineNumbers.Add(LineBase + 1 + i);
            requestIds.Add(request.RequestId);
        }

        // Spent credits stay spent, and the server reads a lower total as a new run, so it is only ever raised.
        var credits = session.Requests.Sum(r => r.CopilotCredits ?? 0);
        var nano    = (long)Math.Round(credits * 1e9, MidpointRounding.AwayFromZero);
        long? staged = null;
        if (nano > sentNanoAiu) {
            lines.Add(CheckpointLine(scope, nano, session.Requests.Count(r => r.CopilotCredits is not null)));
            lineNumbers.Add(LineBase);
            staged = nano;
        }

        return new(lines, lineNumbers, requestIds, staged);
    }

    /// <summary>Stages this drain's usage lines onto the batch, for the recorder to commit once the batch lands. Never
    /// throws: usage is best-effort.</summary>
    public static void Append(WatchState state, List<string> newLines, List<int> newLineNumbers, string transcriptPath, string sessionId, Action<string> log) {
        try {
            if (state.CopilotLocalUsage is null) {
                if (CopilotPaths.ChatSessionLogFor(transcriptPath) is not { } logPath) return;
                state.CopilotLocalUsage = new(new VsCodeChatSessionCache(logPath, Path.GetFileNameWithoutExtension(transcriptPath), log));
            }

            var usage = state.CopilotLocalUsage;
            usage.Unstage();

            if (usage.Log.Current() is not { } session) return;

            var built = Build(sessionId, session, usage.SentRequests, usage.SentNanoAiu);
            newLines.AddRange(built.Lines);
            newLineNumbers.AddRange(built.LineNumbers);
            usage.StagedRequests.AddRange(built.RequestIds);
            usage.StagedNanoAiu = built.NanoAiu;
        } catch (Exception ex) {
            log($"VS Code chat log usage read failed: {ex.Message}");
        }
    }

    static string UsageRecordLine(string scope, string requestId, string model, long outputTokens) => new JsonObject {
        ["type"] = "session.usage_record",
        ["id"]   = Id($"copilot-local-request:{scope}:{requestId}"),
        ["data"] = new JsonObject {
            ["usage"] = new JsonObject {
                ["model"]        = model,
                ["outputTokens"] = outputTokens,
            },
        },
    }.ToJsonString();

    // The watermarks mark the total as session-wide, so a window reload's new run continues it rather than adding to it.
    static string CheckpointLine(string scope, long nanoAiu, int requests) => new JsonObject {
        ["type"] = "session.usage_checkpoint",
        ["id"]   = Id(string.Create(CultureInfo.InvariantCulture, $"copilot-local-credits:{scope}:{nanoAiu}")),
        ["data"] = new JsonObject {
            ["totalNanoAiu"]              = nanoAiu,
            ["usageAccountingWatermarks"] = new JsonObject { ["vscodeChatRequests"] = requests },
        },
    }.ToJsonString();

    static string Id(string key) => TranscriptIds.Hash(Encoding.UTF8.GetBytes(key)).ToString("D");
}
