using Capacitor.Models.Transcripts.Harness.Claude;
using Capacitor.Models.Transcripts.UserMessages;

namespace Capacitor.Cli.Core;

/// The system notes every vendor's chat rules show for a user-channel message that is not the person's.
public static class ChatNotes {
    /// What the trigger said, without its frame; the label says who sent it.
    public static AcpEventEnvelope Trigger(AcpEventEnvelope envelope, UserMessageClass cls, string text) =>
        envelope with {
            Kind     = AcpEventKind.SystemNote,
            Text     = TriggerFrame.Unframed(text),
            ToolKind = cls.Label == UserMessageClassifier.PeerSession ? ChatDisplayKind.PeerSession : ChatDisplayKind.Trigger,
            ToolName = TriggerFrame.Sender(text) is { } sender ? $"Message from {sender}" : cls.Label,
        };

    public static AcpEventEnvelope Interrupted(AcpEventEnvelope envelope) =>
        envelope with { Kind = AcpEventKind.SystemNote, Text = "", ToolKind = ChatDisplayKind.Interrupted };
}
