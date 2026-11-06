namespace Capacitor.Cli.Daemon.Services;

/// <summary>What became of each recent server dispatch, by its id. The server resends an input until it
/// is acknowledged, so a resend of one already taken must be answered, never typed again. Process memory
/// only: the server never resends to another daemon process.
///
/// <para>An entry is kept for <see cref="Retention"/> after the last copy of it arrived, which outlasts the
/// server's resend window, so a resend can never find its id gone. That holds for one still in flight too: no
/// copy can arrive after it is dropped, and a delivery that never settles cannot hold it for the daemon's
/// lifetime.</para></summary>
internal sealed class DispatchedInputLedger(TimeProvider time) {
    // The server stops resending 10 minutes after the first send.
    internal static readonly TimeSpan Retention = TimeSpan.FromMinutes(15);

    readonly Lock                    _gate  = new();
    readonly Dictionary<Guid, Entry> _state = [];

    /// <summary>Records the dispatch as in flight when it is new, and says what an earlier copy came to
    /// otherwise.</summary>
    public DispatchState Admit(Guid dispatchId, out string? dropReason) {
        var now = time.GetUtcNow();

        lock (_gate) {
            Prune(now);

            if (_state.TryGetValue(dispatchId, out var entry)) {
                _state[dispatchId] = entry with { LastSeen = now };
                dropReason         = entry.DropReason;

                return entry.State;
            }

            _state[dispatchId] = new(DispatchState.InFlight, null, now);
            dropReason         = null;

            return DispatchState.New;
        }
    }

    /// <summary>Never undoes a drop: a write lost after the input was taken is final.</summary>
    public void MarkTaken(Guid dispatchId) => Settle(dispatchId, DispatchState.Taken, null);

    public void MarkDropped(Guid dispatchId, string reason) => Settle(dispatchId, DispatchState.Dropped, reason);

    /// <summary>Lets a resend through again: the copy that was in flight never finished.</summary>
    public void Forget(Guid dispatchId) {
        lock (_gate) _state.Remove(dispatchId);
    }

    void Settle(Guid dispatchId, DispatchState state, string? reason) {
        var now = time.GetUtcNow();

        lock (_gate)
            if (_state.TryGetValue(dispatchId, out var entry) && !(state == DispatchState.Taken && entry.State == DispatchState.Dropped))
                _state[dispatchId] = new(state, reason, now);
    }

    void Prune(DateTimeOffset now) {
        List<Guid>? expired = null;

        foreach (var (id, entry) in _state)
            if (now - entry.LastSeen > Retention)
                (expired ??= []).Add(id);

        if (expired is null) return;

        foreach (var id in expired) _state.Remove(id);
    }

    readonly record struct Entry(DispatchState State, string? DropReason, DateTimeOffset LastSeen);
}
