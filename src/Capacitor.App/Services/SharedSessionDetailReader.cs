namespace Capacitor.App.Services;

/// Callers asking for one session's detail while a read of it is in flight share that read. A
/// joiner can get a body that predates its call, so this suits only callers that tail the stream
/// from the position the body ends at. The read is cancelled once every caller waiting on it has
/// gone, and a caller arriving after that starts a new one. A joiner handed a refusal or a miss reads
/// again once: the read it joined may have carried a token refreshed since, or answered before the
/// session existed.
public sealed class SharedSessionDetailReader(SessionDetailReader read) {
    sealed class Flight {
        public readonly CancellationTokenSource Cts = new();
        public Task<SessionDetailFetch> Task = null!;
        public int Waiters;
        public bool Done;
    }

    readonly Dictionary<string, Flight> _flights = new(StringComparer.Ordinal);
    readonly Lock _lock = new();

    public async Task<SessionDetailFetch> ReadAsync(string sessionId, CancellationToken ct) {
        var (fetch, joined) = await ShareAsync(sessionId, ct).ConfigureAwait(false);
        return joined && (fetch.Unauthorized || fetch.NotFound) ? (await ShareAsync(sessionId, ct).ConfigureAwait(false)).Fetch : fetch;
    }

    async Task<(SessionDetailFetch Fetch, bool Joined)> ShareAsync(string sessionId, CancellationToken ct) {
        Flight flight;
        var joined = true;
        lock (_lock) {
            if (!_flights.TryGetValue(sessionId, out flight!)) {
                flight = new Flight();
                _flights[sessionId] = flight;
                flight.Task = Task.Run(() => RunAsync(sessionId, flight));
                joined = false;
            }
            flight.Waiters++;
        }
        try {
            return (await flight.Task.WaitAsync(ct).ConfigureAwait(false), joined);
        } finally {
            lock (_lock) {
                if (--flight.Waiters == 0 && !flight.Done) {
                    Detach(sessionId, flight);
                    flight.Cts.Cancel();
                }
            }
        }
    }

    async Task<SessionDetailFetch> RunAsync(string sessionId, Flight flight) {
        try {
            return await read(sessionId, flight.Cts.Token).ConfigureAwait(false);
        } finally {
            lock (_lock) {
                flight.Done = true;
                Detach(sessionId, flight);
            }
            flight.Cts.Dispose();
        }
    }

    // Caller holds _lock.
    void Detach(string sessionId, Flight flight) {
        if (_flights.TryGetValue(sessionId, out var current) && ReferenceEquals(current, flight)) _flights.Remove(sessionId);
    }
}
