using Capacitor.Models.Transcripts.Harness.Claude;
using Capacitor.Models.Transcripts.UserMessages;

namespace Capacitor.Cli.Core;

/// The system notes every vendor's chat rules show for a user-channel message that is not the person's.
public static class ChatNotes {
    /// What the trigger said, without the frame that only names its sender; the label names it instead.
    public static AcpEventEnvelope Trigger(AcpEventEnvelope envelope, UserMessageClass cls, string text) =>
        envelope with { Kind = AcpEventKind.SystemNote, Text = TriggerFrame.Unframed(text), ToolKind = ChatDisplayKind.Trigger, ToolName = cls.Label };

    public static AcpEventEnvelope Interrupted(AcpEventEnvelope envelope) =>
        envelope with { Kind = AcpEventKind.SystemNote, Text = "", ToolKind = ChatDisplayKind.Interrupted };
}
