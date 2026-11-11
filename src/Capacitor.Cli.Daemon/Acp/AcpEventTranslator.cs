using Capacitor.Cli.Core;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Acp;

/// <summary>
/// Pure, static, per-update translator from a reduced
/// <see cref="AcpSessionUpdate"/> (daemon-local) to the daemon-local wire
/// <see cref="AcpEventEnvelope"/> (Core — field-for-field mirror of the server's
/// <c>Capacitor.Server.Core.Acp.AcpEventEnvelope</c>). Maps exactly ONE update to AT MOST one
/// envelope (see <c>docs/ai688-option-b-canonical-surfacing-design.md</c>). Deliberately does
/// NOT aggregate chunk streams, correlate multi-update tool-call state, assign real sequence
/// numbers, or forward anything — those are handled elsewhere (aggregation/seq assignment is
/// runtime-owned; <c>seq</c>/<c>timestampIso</c> below are caller-supplied
/// inputs, not derived here). <see cref="Translate"/> never throws: an unmappable/dropped kind
/// returns <see langword="null"/> rather than fabricating an empty envelope.
/// </summary>
internal static partial class AcpEventTranslator {
    /// <summary>
    /// ACP <c>ToolCallStatus</c> values that represent a FINISHED tool call —
    /// <c>pending</c>/<c>in_progress</c>/a missing status are non-terminal, status-only updates that
    /// never emit a <c>ToolResult</c> envelope, regardless of whether <see cref="AcpSessionUpdate.ToolResultText"/>
    /// happens to be set.
    /// </summary>
    static bool IsTerminalToolStatus(string? status) => status is "completed" or "failed";

    /// <summary>
    /// Chunk aggregation belongs to the caller; use its aggregate when supplied.
    /// ACP tool calls expose a display title rather than a machine name, so the title fills ToolName.
    /// Emit tool results only for terminal statuses with extractable content; status-only updates
    /// must not fabricate empty results. Unknown tool kinds normalize to the closed vocabulary.
    /// Unknown update JSON can contain prompts, files, or tool output: log its content only when
    /// <paramref name="debugFrames"/> is enabled; otherwise log its length.
    /// </summary>
    public static AcpEventEnvelope? Translate(
            AcpSessionUpdate update,
            long             seq,
            string           timestampIso,
            string?          aggregatedText = null,
            ILogger?         logger         = null,
            bool             debugFrames    = false,
            string?          resolvedModel  = null) {
        switch (update.Kind) {
            case AcpUpdateKind.AgentMessageChunk:
                return new AcpEventEnvelope(
                    Seq: seq,
                    Kind: AcpEventKind.AssistantText,
                    Text: aggregatedText ?? update.Text,
                    TimestampIso: timestampIso);

            case AcpUpdateKind.AgentThoughtChunk:
                return new AcpEventEnvelope(
                    Seq: seq,
                    Kind: AcpEventKind.AssistantThinking,
                    Text: aggregatedText ?? update.Text,
                    TimestampIso: timestampIso);

            case AcpUpdateKind.ToolCall:
                return new AcpEventEnvelope(
                    Seq: seq,
                    Kind: AcpEventKind.ToolCall,
                    ToolCallId: update.ToolCallId,
                    ToolName: update.ToolTitle,
                    ToolInputJson: update.ToolInputJson,
                    ToolKind: AcpToolKind.Normalize(update.ToolKind),
                    TimestampIso: timestampIso);

            case AcpUpdateKind.ToolCallUpdate:
                if (!IsTerminalToolStatus(update.ToolStatus) || update.ToolResultText is null)
                    return null; // status-only update — never an empty ToolResultReceived

                return new AcpEventEnvelope(
                    Seq: seq,
                    Kind: AcpEventKind.ToolResult,
                    ToolCallId: update.ToolCallId,
                    ToolResult: update.ToolResultText,
                    ToolIsError: update.ToolIsError,
                    TimestampIso: timestampIso);

            case AcpUpdateKind.UsageUpdate:
                // Reduce() already validated both fields, so a UsageUpdate here is always emittable.
                // The resolved model is stamped on EVERY usage envelope: the server's mapper is a
                // pure per-envelope function with no session-fold access, so attribution has to
                // travel on the wire rather than be looked up server-side. A null model is
                // harmless - the chip's denominator is the reading's own window.
                return new AcpEventEnvelope(
                    Seq: seq,
                    Kind: AcpEventKind.Usage,
                    Model: resolvedModel,
                    ContextUsedTokens: update.ContextUsedTokens,
                    ContextWindowTokens: update.ContextWindowTokens,
                    TimestampIso: timestampIso);

            case AcpUpdateKind.SessionInfo:
                // session_info_update carries the agent's auto-generated session title. Emit a
                // SessionTitle envelope only when a non-blank title is present; a title-less info
                // update has nothing to surface, so it is dropped like the other content-free kinds.
                return string.IsNullOrWhiteSpace(update.Title)
                    ? null
                    : new AcpEventEnvelope(
                        Seq: seq,
                        Kind: AcpEventKind.SessionTitle,
                        Text: update.Title,
                        TimestampIso: timestampIso);

            case AcpUpdateKind.Plan:
            case AcpUpdateKind.AvailableCommands:
                return null;

            case AcpUpdateKind.Unknown:
            default:
                // Gate on IsEnabled(Debug) first so the default path never materializes the raw JSON
                // (GetRawText allocates the full payload) when Debug logging is disabled.
                if (logger is not null && logger.IsEnabled(LogLevel.Debug)) {
                    // KCAP_ACP_DEBUG_FRAMES gate (Off by default): the raw update JSON can carry
                    // prompt/tool/file content, so it is only ever logged verbatim when the operator
                    // has explicitly opted in — otherwise this logs shape (length) only.
                    if (debugFrames)
                        LogUnknownUpdateFull(logger, AcpDebugFrameLog.Cap(update.Raw?.GetRawText() ?? ""));
                    else
                        LogUnknownUpdateShape(logger, update.Raw?.GetRawText()?.Length ?? 0);
                }

                return null;
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "ACP: dropping unrecognized session/update kind (Unknown); Raw={Raw}")]
    static partial void LogUnknownUpdateFull(ILogger logger, string raw);

    [LoggerMessage(Level = LogLevel.Debug, Message = "ACP: dropping unrecognized session/update kind (Unknown); RawLength={RawLength} chars")]
    static partial void LogUnknownUpdateShape(ILogger logger, int rawLength);

    /// <summary>
    /// Builds the daemon-synthesized <c>SessionStarted</c> envelope — NOT derived
    /// from an <see cref="AcpSessionUpdate"/> (ACP's <c>session/update</c> stream never carries a
    /// session-started variant). The orchestrator calls this exactly once per session, at
    /// <c>Seq 0</c>, AFTER agent registration and paired with the <c>AcpSessionStarted</c> hub bind —
    /// this builder itself has no ordering opinion; it is a pure envelope constructor.
    /// </summary>
    public static AcpEventEnvelope BuildSessionStarted(
            long    seq,
            string  timestampIso,
            string? cwd          = null,
            string? model        = null,
            string? rawSessionId = null,
            string? sessionMode  = null,
            HostedSessionRepository? repository = null) =>
        new(
            Seq: seq,
            Kind: AcpEventKind.SessionStarted,
            Cwd: cwd,
            Model: model,
            RawSessionId: rawSessionId,
            SessionMode: sessionMode,
            TimestampIso: timestampIso,
            RepoOwner: repository?.Owner,
            RepoName: repository?.Name,
            RepoRemoteUrl: repository?.RemoteUrl,
            RepoBranch: repository?.Branch);

    /// <summary>
    /// Builds the daemon-synthesized <c>UserMessage</c> envelope — one per
    /// serialized prompt turn, since ACP's <c>session/prompt</c> request itself never
    /// round-trips through <c>session/update</c> and so has no natural "update" to translate.
    /// </summary>
    public static AcpEventEnvelope BuildUserMessage(long seq, string timestampIso, string text) =>
        new(
            Seq: seq,
            Kind: AcpEventKind.UserMessage,
            Text: text,
            TimestampIso: timestampIso);
}
