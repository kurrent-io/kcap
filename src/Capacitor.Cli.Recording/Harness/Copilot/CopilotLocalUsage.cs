using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness.Copilot;

namespace Capacitor.Cli.Recording.Harness.Copilot;

/// <summary>
/// A VS Code Local transcript carries no usage; the session's chat log does, written seconds after the transcript's
/// last line. Each drain turns what the log holds into lines the server reads: a <c>session.usage_record</c> per
/// completed request (its output and the model it ran on) and a <c>kcap.vscode_request_credits</c> per request whose
/// credits rose. Credits go per request because the log can lose a request the user deletes, so no session total
/// read from it can be trusted not to fall. Each line's id is derived from what it reports, so a live send and a
/// re-import of the same figure dedupe.
/// </summary>
public static class CopilotLocalUsage {
    // Clear of real line numbers and of the Antigravity (1e9) and Kiro (2e9) bands; the server keeps lines this high out
    // of its transcript watermark. A request's line number comes from its id, so a line already sent keeps its number
    // however the log's requests shift, and the server's retry of a failed line by number retries that line.
    public const int LineBase = 1_500_000_000;
    const int SubBand = 200_000_000;

    public static int UsageRecordLineNumber(string requestId) => LineBase + 1 + Slot(requestId);

    public static int CreditsLineNumber(string requestId) => LineBase + 1 + SubBand + Slot(requestId);

    static int Slot(string requestId) =>
        (int)(BitConverter.ToUInt32(TranscriptIds.Hash(Encoding.UTF8.GetBytes(requestId)).ToByteArray(), 0) % SubBand);

    public static CopilotLocalUsageLines Build(
            string sessionId, VsCodeChatSession session, IReadOnlySet<string> sentRequests, IReadOnlyDictionary<string, long> sentCredits) {
        var scope       = sessionId.Replace("-", "").ToLowerInvariant();
        var lines       = new List<string>();
        var lineNumbers = new List<int>();
        var requestIds  = new List<string>();
        var credits     = new List<(string, long)>();
        var seen        = new HashSet<string>(StringComparer.Ordinal);

        foreach (var request in session.Requests) {
            if (!seen.Add(request.RequestId)) continue;

            if (request.Complete && request.CompletionTokens is (> 0 and var tokens) && request.Model is { } model
             && !sentRequests.Contains(request.RequestId)) {
                lines.Add(UsageRecordLine(scope, request.RequestId, model, tokens));
                lineNumbers.Add(UsageRecordLineNumber(request.RequestId));
                requestIds.Add(request.RequestId);
            }

            // A request's credits only grow; the server keeps each request's largest figure.
            if (request.CopilotCredits is { } spent && (long)Math.Round(spent * 1e9, MidpointRounding.AwayFromZero) is > 0 and var nano
             && (!sentCredits.TryGetValue(request.RequestId, out var sent) || nano > sent)) {
                lines.Add(CreditsLine(scope, request.RequestId, nano));
                lineNumbers.Add(CreditsLineNumber(request.RequestId));
                credits.Add((request.RequestId, nano));
            }
        }

        return new(lines, lineNumbers, requestIds, credits);
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

            var built = Build(sessionId, session, usage.SentRequests, usage.SentCredits);
            newLines.AddRange(built.Lines);
            newLineNumbers.AddRange(built.LineNumbers);
            usage.StagedRequests.AddRange(built.RequestIds);
            usage.StagedCredits.AddRange(built.Credits);
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

    static string CreditsLine(string scope, string requestId, long nanoAiu) => new JsonObject {
        ["type"] = "kcap.vscode_request_credits",
        ["id"]   = Id(string.Create(CultureInfo.InvariantCulture, $"copilot-local-credits:{scope}:{requestId}:{nanoAiu}")),
        ["data"] = new JsonObject {
            ["requestId"]    = requestId,
            ["totalNanoAiu"] = nanoAiu,
        },
    }.ToJsonString();

    static string Id(string key) => TranscriptIds.Hash(Encoding.UTF8.GetBytes(key)).ToString("D");
}
