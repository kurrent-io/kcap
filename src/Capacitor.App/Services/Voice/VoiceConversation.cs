namespace Capacitor.App.Services.Voice;

/// The voice conversation, held in memory for the app's run: what the user said, what the app
/// answered and what the agents said aloud. The last few turns go to the interpreter with each
/// command, and a bare "yes" or "no" answers the latest of them.
public sealed class VoiceConversation(TimeProvider time) {
    /// Turns older than this are not context any more: a "the other one" said an hour later means nothing.
    static readonly TimeSpan ContextWindow = TimeSpan.FromMinutes(10);
    const int ContextTurns = 8;
    const int Kept = 200;

    readonly Lock _lock = new();
    readonly List<VoiceExchange> _turns = [];

    /// The turns an interpreter is told about: the last few, from the last few minutes.
    public IReadOnlyList<VoiceExchange> Recent() {
        var since = time.GetUtcNow() - ContextWindow;
        lock (_lock) return _turns.Where(t => t.At >= since).TakeLast(ContextTurns).ToArray();
    }

    /// The latest turn, unless it is older than <paramref name="within"/>.
    public VoiceExchange? Last(TimeSpan within) {
        var since = time.GetUtcNow() - within;
        lock (_lock) return _turns.Count > 0 && _turns[^1].At >= since ? _turns[^1] : null;
    }

    public void Add(string said, string answer) => Add(new VoiceExchange(time.GetUtcNow(), said, answer));

    /// A command and the app's response to it.
    public void Add(string said, VoiceResponse response) => Add(new VoiceExchange(time.GetUtcNow(), said, response.Speech) {
        Speaker = response.Speaker,
        Question = response is { FollowUp: VoiceFollowUp.Confirm, OnConfirm: not null } ? response : null,
    });

    /// Something an agent said aloud without being asked.
    public void AddAgent(VoiceSession? speaker, string text) =>
        Add(new VoiceExchange(time.GetUtcNow(), "", text) { Speaker = speaker });

    void Add(VoiceExchange turn) {
        if (turn.Said.Length == 0 && turn.Answer.Length == 0) return;
        lock (_lock) {
            _turns.Add(turn);
            if (_turns.Count > Kept) _turns.RemoveRange(0, _turns.Count - Kept);
        }
    }
}
