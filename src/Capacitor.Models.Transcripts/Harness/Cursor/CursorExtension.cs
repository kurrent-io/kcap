using Google.Protobuf.WellKnownTypes;

namespace Capacitor.Models.Transcripts.Harness.Cursor;

/// The `cursor` extension slug on a user message, shaped as the server's turn record serializes: every field
/// present, null when the hook gave none. The server's read side unpacks it into that record.
public static class CursorExtension {
    public const string Slug = "cursor";

    public static Struct Turn(CursorTurn turn) => new() {
        Fields = {
            ["generation_id"] = Value.ForString(turn.GenerationId),
            ["attachments"]   = Value.ForList([.. turn.Attachments.Select(Attachment)]),
        },
    };

    static Value Attachment(CursorTurnAttachment attachment) => Value.ForStruct(new Struct {
        Fields = {
            ["type"]      = Value.ForString(attachment.Type),
            ["file_path"] = attachment.FilePath is { } path ? Value.ForString(path) : Value.ForNull(),
        },
    });
}
