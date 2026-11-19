using Capacitor.App.Services;
using Capacitor.Remote.Models;

namespace Capacitor.App.Tests.Unit;

/// The server's session events route over a list of events: `from` counts from the oldest event held,
/// a page stops at the limit or at the server's cap, and next_from is set while more remain.
sealed class FakeSessionEvents(params SessionEventDto[] events) {
    readonly Lock _lock = new();
    public List<SessionEventDto> Events { get; } = [.. events];
    public List<(long From, int Limit)> Requests { get; } = [];
    /// Overrides the last event number reported while the list is empty.
    public long EmptyLastEventNumber { get; set; } = -1;
    public bool NotFound { get; set; }
    public bool Unauthorized { get; set; }
    /// How many of the next requests fail as an unreachable server would.
    public int FailNext { get; set; }
    /// Each request waits for this before answering, when set.
    public Func<Task>? Gate { get; set; }

    /// A null limit reads as the server's cap.
    public async Task<StreamEventsFetch> Read(string sessionId, long from, int? asked, CancellationToken ct) {
        var limit = asked ?? 1000;
        lock (_lock) Requests.Add((from, limit));
        if (Gate is { } gate) await gate();
        lock (_lock) {
            if (FailNext > 0) {
                FailNext--;
                return new(null, Failure: "HTTP 503");
            }
        }
        if (NotFound) return new(null, NotFound: true);
        if (Unauthorized) return new(null, Unauthorized: true);
        lock (_lock) return new(Page(Events, Events.Count == 0 ? EmptyLastEventNumber : Events[^1].EventNumber, from, limit));
    }

    public static StreamEventsPage Page(IReadOnlyList<SessionEventDto> events, long lastEventNumber, long from, int limit) {
        var start = (int)Math.Min(from, events.Count);
        var count = Math.Min(Math.Min(limit, 1000), events.Count - start);
        return new() {
            LastEventNumber = lastEventNumber,
            Events = events.Skip(start).Take(count).ToArray(),
            NextFrom = start + count < events.Count ? start + count : null,
        };
    }

    /// Pages a whole-session document the way the events route would, one detail read per page.
    public static SessionEventsReader Paged(Func<CancellationToken, Task<SessionDetailFetch>> read) => Paged((_, ct) => read(ct));

    /// The same, for a document that depends on which session is asked for.
    public static SessionEventsReader Paged(Func<string, CancellationToken, Task<SessionDetailFetch>> read) => async (sessionId, from, limit, ct) => {
        var fetch = await read(sessionId, ct);
        if (fetch.Detail is not { } detail) return new(null, fetch.NotFound, fetch.Unauthorized);
        return new(Page(detail.Events ?? [], detail.LastEventNumber, from, limit ?? 1000));
    };

    public SessionEventsReader Reader => Read;

    public int RequestCount { get { lock (_lock) return Requests.Count; } }
}
