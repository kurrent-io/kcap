namespace Capacitor.Cli.Daemon.Services;

/// <summary>What became of each recent server dispatch, by its id. The server resends an input until it
/// is acknowledged, so a resend of one already taken must be answered, never typed again. Process memory
/// only: the server never resends to another daemon process.</summary>
internal sealed class DispatchedInputLedger {
    internal const int Capacity = 4096;

    readonly Lock                            _gate  = new();
    readonly Dictionary<Guid, Entry>         _state = [];
    readonly Queue<Guid>                     _order = new();

    /// <summary>Records the dispatch as in flight when it is new, and says what an earlier copy came to
    /// otherwise.</summary>
    public DispatchState Admit(Guid dispatchId, out string? dropReason) {
        lock (_gate) {
            if (_state.TryGetValue(dispatchId, out var entry)) {
                dropReason = entry.DropReason;

                return entry.State;
            }

            _state[dispatchId] = new(DispatchState.InFlight, null);
            _order.Enqueue(dispatchId);

            while (_order.Count > Capacity) _state.Remove(_order.Dequeue());

            dropReason = null;

            return DispatchState.New;
        }
    }

    public void MarkTaken(Guid dispatchId) => Set(dispatchId, new(DispatchState.Taken, null));

    public void MarkDropped(Guid dispatchId, string reason) => Set(dispatchId, new(DispatchState.Dropped, reason));

    /// <summary>Lets a resend through again: the copy that was in flight never finished.</summary>
    public void Forget(Guid dispatchId) {
        lock (_gate) _state.Remove(dispatchId);
    }

    void Set(Guid dispatchId, Entry entry) {
        lock (_gate)
            if (_state.ContainsKey(dispatchId)) _state[dispatchId] = entry;
    }

    readonly record struct Entry(DispatchState State, string? DropReason);
}
