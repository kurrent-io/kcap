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

    /// Answers a tail request as a server that predates the parameter would: a page from `from`, with no first_from.
    public bool IgnoresTail { get; set; }
    public List<int> Tails { get; } = [];

    /// A null limit reads as the server's cap.
    public async Task<StreamEventsFetch> Read(string sessionId, long from, int? asked, int? tail, CancellationToken ct) {
        var limit = tail is not null && IgnoresTail ? 500 : asked ?? 1000;
        lock (_lock) {
            if (tail is { } t && !IgnoresTail) {
                Tails.Add(t);
                // Recorded in from/limit terms too, so a test counting requests sees the tail page.
                var count = Math.Min(t, 1000);
                var start = Math.Max(from, Events.Count - count);
                Requests.Add((start, count));
            } else {
                Requests.Add((from, limit));
            }
        }
        if (Gate is { } gate) await gate();
        lock (_lock) {
            if (FailNext > 0) {
                FailNext--;
                return new(null, Failure: "HTTP 503");
            }
        }
        if (NotFound) return new(null, NotFound: true);
        if (Unauthorized) return new(null, Unauthorized: true);
        lock (_lock) {
            var last = Events.Count == 0 ? EmptyLastEventNumber : Events[^1].EventNumber;
            if (tail is not { } n || IgnoresTail) return new(Page(Events, last, from, limit));
            var count = Math.Min(n, 1000);
            var start = Math.Max(from, Math.Max(0, Events.Count - count));
            return new(Page(Events, last, start, count) with { FirstFrom = (int)Math.Min(start, Events.Count) });
        }
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
    public static SessionEventsReader Paged(Func<string, CancellationToken, Task<SessionDetailFetch>> read) => async (sessionId, from, limit, tail, ct) => {
        var fetch = await read(sessionId, ct);
        if (fetch.Detail is not { } detail) return new(null, fetch.NotFound, fetch.Unauthorized);
        var events = detail.Events ?? [];
        if (tail is not { } n) return new(Page(events, detail.LastEventNumber, from, limit ?? 1000));
        var start = Math.Max(from, Math.Max(0, events.Length - Math.Min(n, 1000)));
        return new(Page(events, detail.LastEventNumber, start, Math.Min(n, 1000)) with { FirstFrom = (int)Math.Min(start, events.Length) });
    };

    public SessionEventsReader Reader => Read;

    public int RequestCount { get { lock (_lock) return Requests.Count; } }
}
