namespace Capacitor.App.Services.Voice;

/// Something a session the user prompted by voice wants them to hear.
public abstract record VoiceNarration {
    /// The agent finished its turn; the text is its last reply.
    public sealed record Reply(string Text) : VoiceNarration;
    /// The agent is waiting on the user: a permission request or a question.
    public sealed record Attention(string Text) : VoiceNarration;
}
