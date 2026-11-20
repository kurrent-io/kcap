namespace Capacitor.Cli.Core;

/// What the chat tab needs from any transcript reader: a per-file context and a line-to-envelopes step.
public interface IChatTranscriptProjection {
    TranscriptContext CreateContext(string sessionId, string? agentId);
    IReadOnlyList<AcpEventEnvelope> Project(string line, int lineNumber, DateTimeOffset receivedAt, TranscriptContext context);

    ChatProjectionResult ProjectWithInputs(string line, int lineNumber, DateTimeOffset receivedAt, TranscriptContext context) {
        var envelopes = Project(line, lineNumber, receivedAt, context);
        return new(envelopes, envelopes.Where(e => e is { Kind: AcpEventKind.UserMessage, Text: not null }).Select(e => e.Text!).ToArray(), []);
    }
}
