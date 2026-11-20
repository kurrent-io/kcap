namespace Capacitor.Cli.Core.Harness.Copilot;

/// <summary>One request of a VS Code chat session, with each figure null when the log did not carry it in the
/// expected shape.</summary>
/// <param name="CompletionTokens">The request's output across all its model calls. The extension's own
/// <c>result.metadata.outputTokens</c> counts only the last call, so it is never read.</param>
/// <param name="SentAtMs">When the request was sent, in Unix milliseconds; VS Code copies it verbatim into a chat it
/// moves from Local to Copilot.</param>
/// <param name="Text">The prompt as the person typed it.</param>
/// <param name="Model">The model the request ran on, never the <c>copilot/auto</c> alias it was sent to.</param>
public sealed record VsCodeChatRequest(
    string RequestId,
    bool Complete,
    long? CompletionTokens,
    double? CopilotCredits,
    long? SentAtMs,
    string? Text,
    string? Model
);
