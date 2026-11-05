namespace Capacitor.App.Services.Voice;

/// What voice control says back after a command, and what it waits for next.
public sealed record VoiceResponse(string Speech, bool Failed = false) {
    public VoiceFollowUp FollowUp { get; init; }

    /// Runs on a yes, when <see cref="FollowUp"/> is Confirm.
    public Func<Task<VoiceResponse>>? OnConfirm { get; init; }

    /// The command sent a turn: narrate what the session says next.
    public bool WatchTurn { get; init; }

    /// A long reply is being read in parts; the next few seconds accept "more" and the like
    /// without the wake word.
    public bool OpensReply { get; init; }

    /// Read out rather than answered: a reply or a list, which may use the slower, natural voice.
    public bool Reads { get; init; }

    /// The session whose agent's reply Speech reads out; a "yes" after it answers that agent.
    public VoiceSession? Speaker { get; init; }

    /// How long a question waits for its answer, when not the usual few seconds.
    public TimeSpan? Window { get; init; }

    public static VoiceResponse Fail(string speech) => new(speech, Failed: true);
}
