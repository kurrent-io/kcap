using Capacitor.Models.Transcripts.Harness.Claude;
using Kurrent.Agent.Schema.Events;

namespace Capacitor.Models.Transcripts.UserMessages;

/// Tells the transcript rows a person wrote from the rest, through the vendor's own projection, and gives each one's
/// text as the person entered it, so it compares with what was pasted into the terminal
public sealed class PersonRows(ITranscriptProjection projection, string sessionId, string vendor) {
    /// The text of the person's message <paramref name="line"/> records, or null for every other line. A slash command
    /// is its command line as typed. Each line is projected on its own, so the answer never depends on the lines before
    public string? Text(string line, int lineNumber) {
        var projected = projection.Project(line, lineNumber, DateTimeOffset.UnixEpoch, projection.CreateContext(sessionId, null));
        if (projected.Events.Select(e => e.Payload).OfType<UserMessageReceived>().FirstOrDefault() is not { } message) return null;
        if (SchemaExtensions.Flag(SchemaExtensions.Slug(message, ClaudeCodeExtension.Slug), ClaudeCodeExtension.IsSidechain)) return null;

        // The classifier calls a receipt the harness carrying on, since it opens no turn, but Claude Code records a
        // command the person ran in nothing else, and the projection keeps only a person's receipts
        if (UserMessageOrigin.Of(message)?.LocalCommand is { } receipt) return receipt;
        if (UserMessageClassifier.Classify(message, vendor).Kind != UserMessageKind.Person) return null;

        var content = message.Content ?? "";

        return LocalCommandParser.IsPure(content, out var command) && command.Kind == LocalCommandParser.LocalCommandKind.Command
            ? LocalCommandParser.DisplayLabel(command)
            : content;
    }

    /// Whether a row carrying <paramref name="text"/> as a person's own message would be classed as a person's
    public static bool IsPersonText(string text, string vendor) =>
        UserMessageClassifier.Classify(text, new UserMessageOrigin { Kind = "human" }, vendor).Kind == UserMessageKind.Person;
}
