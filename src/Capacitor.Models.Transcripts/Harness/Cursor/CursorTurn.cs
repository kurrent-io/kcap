namespace Capacitor.Models.Transcripts.Harness.Cursor;

/// What Cursor's <c>beforeSubmitPrompt</c> hook reported for one prompt and the transcript does not carry: the
/// generation it started and the prompt's attachments.
public sealed record CursorTurn(string GenerationId, IReadOnlyList<CursorTurnAttachment> Attachments);
