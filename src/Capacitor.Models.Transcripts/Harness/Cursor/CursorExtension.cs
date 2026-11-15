using Google.Protobuf.WellKnownTypes;

namespace Capacitor.Models.Transcripts.Harness.Cursor;

/// The `cursor` extension slug on a user message, shaped as the server's turn record serializes: every field
/// present, null when the hook gave none. The server's read side unpacks it into that record.
public static class CursorExtension {
    public const string Slug = "cursor";

    public static Struct Turn(CursorTurn turn) {
        var block = new Struct();
        block.Fields["generation_id"] = Value.ForString(turn.GenerationId);
        block.Fields["attachments"]   = Value.ForList([.. turn.Attachments.Select(Attachment)]);
        return block;
    }

    static Value Attachment(CursorTurnAttachment attachment) {
        var item = new Struct();
        item.Fields["type"]      = Value.ForString(attachment.Type);
        item.Fields["file_path"] = attachment.FilePath is { } path ? Value.ForString(path) : Value.ForNull();
        return Value.ForStruct(item);
    }
}
