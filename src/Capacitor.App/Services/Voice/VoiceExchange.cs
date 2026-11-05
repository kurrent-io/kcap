namespace Capacitor.App.Services.Voice;

/// One turn of the voice conversation: what the user said, if anything, and what was said back.
public sealed record VoiceExchange(DateTimeOffset At, string Said, string Answer) {
    /// The session whose agent's words Answer is; null when the app said it.
    public VoiceSession? Speaker { get; init; }

    /// The yes-or-no question the app asked in Answer.
    public VoiceResponse? Question { get; init; }
}
