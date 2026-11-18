namespace Capacitor.Models.Transcripts.Harness.Cursor;

/// One attachment Cursor's <c>beforeSubmitPrompt</c> hook reported for a prompt: its kind (a rule, a file) and,
/// for one on disk, its path.
public sealed record CursorTurnAttachment(string Type, string? FilePath);
