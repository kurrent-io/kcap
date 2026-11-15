using Capacitor.Cli.Core.Plans;
using Capacitor.Cli.Core.WorkItems;
using Capacitor.Client;

namespace Capacitor.Cli.Ide;

/// <summary>
/// One client subscription to a session's work context: a reader per section, the sections as they stand and as they
/// were last sent. Every member runs on the host's serial lane, which is also where both readers settle, so results
/// apply in the order they arrive.
/// </summary>
internal sealed class ContextSubscription : IDisposable {
    readonly SingleFlightReader<string, WorkContextRead> _work;
    readonly SingleFlightReader<string, SessionPlansRead> _plans;
    readonly ITimer _poll;
    readonly string? _serverUrl;
    readonly Action<ContextSubscription> _changed;
    WorkContextCard _card = WorkContextCard.Empty(WorkContextPhase.Loading);
    WorkSection? _sentWork;
    PlanSection? _sentPlan;
    bool _planAnswered;

    /// <param name="post">Runs a settling read on the host's serial lane.</param>
    /// <param name="changed">Called on that lane whenever a section may have changed.</param>
    /// <param name="signedOut">Called off that lane, before the read settles, when either read answers signed out.</param>
    public ContextSubscription(
            string id, string sessionId, IWorkContextSource work, IPlanSource plans, string? serverUrl, TimeProvider time,
            Func<Action, Task> post, Action<ContextSubscription> changed, Action signedOut) {
        Id = id;
        SessionId = sessionId;
        _serverUrl = serverUrl;
        _changed = changed;
        Work = ContextMapping.Work(_card, serverUrl);
        // Task.Run keeps a source's synchronous prefix off the request loop. A read that finishes before the reader awaits
        // it still settles inside the reader's start, on the host lock its caller holds, so it only lands sooner.
        _work = new(
            (session, ct) => Task.Run(async () => {
                var read = await work.ReadAsync(session, ct);
                if (read.Kind == WorkContextReadKind.SignedOut) signedOut();
                return read;
            }),
            error => WorkContextRead.Of(WorkContextReadKind.Unreachable, Logged("work context", error)),
            ApplyWork, "ide work context", post, cached: work.Cached);
        _plans = new(
            (session, ct) => Task.Run(async () => {
                var read = await plans.ReadAsync(session, ct);
                if (read.Kind == SessionPlansReadKind.SignedOut) signedOut();
                return read;
            }),
            error => {
                Logged("plan", error);
                return SessionPlansRead.Of(SessionPlansReadKind.Unreachable);
            },
            ApplyPlan, "ide plan", post, cached: plans.Cached);
        _poll = time.CreateTimer(_ => post(Poll), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public string Id { get; }

    /// <summary>In the form the server files sessions under.</summary>
    public string SessionId { get; }

    public WorkSection Work { get; private set; }

    public PlanSection Plan { get; private set; } = ContextMapping.LoadingPlan;

    internal Task PendingReadsForTesting =>
        Task.WhenAll(_work.PendingReadForTesting ?? Task.CompletedTask, _plans.PendingReadForTesting ?? Task.CompletedTask);

    /// <summary>Starts both reads. A section the source still holds a read for shows it at once.</summary>
    public void Start() {
        _work.Switch(SessionId);
        _plans.Switch(SessionId);
        _poll.Change(WorkContextTiming.PollInterval, WorkContextTiming.PollInterval);
    }

    /// <summary>Reads both sections now, or once more after a read in flight.</summary>
    public void Refresh() {
        _work.Refresh();
        _plans.Refresh();
    }

    /// <summary>Shows both sections loading and reads them afresh; a read in flight settles into nothing. Call once the
    /// sources hold nothing of the previous credential, or the restart seeds from it.</summary>
    public void Restart() {
        _card = WorkContextCard.Empty(WorkContextPhase.Loading);
        Work = ContextMapping.Work(_card, _serverUrl);
        Plan = ContextMapping.LoadingPlan;
        _planAnswered = false;
        _changed(this);
        _work.Switch(SessionId);
        _plans.Switch(SessionId);
    }

    /// <summary>Both sections as they stand, from here on the baseline for <see cref="TakeChanges"/>.</summary>
    public ContextSnapshot TakeSnapshot() {
        _sentWork = Work;
        _sentPlan = Plan;

        return new ContextSnapshot(Id, SessionId, Work, Plan);
    }

    /// <summary>The sections that differ from the ones last sent, now counted as sent; null when none does or no snapshot
    /// has been taken.</summary>
    public ContextChanged? TakeChanges() {
        if (_sentWork is null || _sentPlan is null) return null;

        var work = Work == _sentWork ? null : Work;
        var plan = Plan == _sentPlan ? null : Plan;
        if (work is null && plan is null) return null;

        _sentWork = Work;
        _sentPlan = Plan;

        return new ContextChanged(Id, work, plan);
    }

    /// <summary>Stops the poll; a read still in flight settles into nothing.</summary>
    public void Dispose() {
        _poll.Dispose();
        _work.ClearCurrent();
        _plans.ClearCurrent();
    }

    void Poll() {
        _work.StartIfIdle();
        _plans.StartIfIdle();
    }

    void ApplyWork(WorkContextRead read) {
        _card = WorkContextCards.Apply(_card, read, null);
        Work = ContextMapping.Work(_card, _serverUrl);
        _changed(this);
    }

    /// <summary>Once any plan read has answered, a read with no answer changes nothing; before that it marks the plan
    /// unavailable.</summary>
    void ApplyPlan(SessionPlansRead read) {
        switch (read.Kind) {
            case SessionPlansReadKind.Ready when PlanBoard.From(read) is { } board:
                Plan = ContextMapping.Plan(board);
                _planAnswered = true;
                break;
            case SessionPlansReadKind.Ready or SessionPlansReadKind.Unavailable:
                Plan = ContextMapping.NoPlan;
                _planAnswered = true;
                break;
            case SessionPlansReadKind.SignedOut:
                Plan = ContextMapping.SignedOutPlan;
                _planAnswered = true;
                break;
            default:
                if (!_planAnswered) Plan = ContextMapping.UnavailablePlan;
                break;
        }

        _changed(this);
    }

    static string Logged(string what, Exception error) {
        try {
            Console.Error.WriteLine($"kcap ide: reading the {what} failed: {error.Message}");
        } catch {
            // A broken stderr is not a protocol fault.
        }

        return error.Message;
    }
}
