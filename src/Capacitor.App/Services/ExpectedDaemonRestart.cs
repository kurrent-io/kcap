using System.Reactive.Linq;
using System.Reactive.Subjects;

namespace Capacitor.App.Services;

/// <summary>
/// Tells an outage the daemon chose apart from one it suffered. A daemon that queued a restart after
/// an update leaves its restart-pending marker on disk until its successor starts, and an app that has
/// just been updated expects its daemon to follow; while either holds, an unreachable daemon is
/// reported with <see cref="Reason"/> instead of <c>daemon_unreachable</c>, and the attach is retried
/// every <see cref="RetryInterval"/> rather than on the connect backoff. A daemon whose pid file names
/// a live process is still starting, as <c>kcap daemon status</c> reads it, and is reported with
/// <see cref="StartingReason"/> — unless that process is the one the app was last attached to, which is
/// stopping. Past <see cref="Grace"/> either reads as an ordinary outage again, so a daemon that never
/// comes back still gets its Start button.
/// </summary>
public sealed class ExpectedDaemonRestart : IDisposable {
    public const string Reason = "daemon_restarting";
    public const string StartingReason = "daemon_starting";
    const string UnreachableReason = "daemon_unreachable";

    internal static readonly TimeSpan Grace = TimeSpan.FromSeconds(90);
    internal static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(1);

    readonly Func<bool> _appJustUpdated;
    readonly Action<bool, TimeSpan>? _outageEnded;
    readonly Func<Task> _reattach;
    readonly Func<int?>? _livePid;
    readonly TimeProvider _time;
    readonly ReplaySubject<AttachStatus> _status = new(1);
    readonly BehaviorSubject<bool> _starting = new(false);
    readonly Lock _lock = new();
    readonly IDisposable _subscription;
    readonly ITimer _timer;

    AttachStatus? _raw;
    bool _marker;
    // From the first unreachable report after the last Connected: a retry's Connecting does not end the outage.
    DateTimeOffset? _outageSince;
    string? _outageReason;
    bool _outageExpected;
    // When a daemon process other than the last attached one was first seen alive in this outage.
    DateTimeOffset? _startingSince;
    int? _attachedPid;
    bool _disposed;

    /// <param name="outageEnded">Told, on reconnecting, whether the outage was expected and how long it lasted.</param>
    /// <param name="livePid">The pid the daemon's pid file names while that process is alive, else null. Null
    /// never reports a starting daemon.</param>
    public ExpectedDaemonRestart(
            IObservable<AttachStatus> status, IObservable<bool> restartPending, Func<bool> appJustUpdated,
            Func<Task> reattach, TimeProvider time, Action<bool, TimeSpan>? outageEnded = null,
            Func<int?>? livePid = null) {
        _appJustUpdated = appJustUpdated;
        _outageEnded = outageEnded;
        _reattach = reattach;
        _livePid = livePid;
        _time = time;
        _timer = time.CreateTimer(_ => Tick(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _subscription = new System.Reactive.Disposables.CompositeDisposable(
            restartPending.Subscribe(pending => Update(() => _marker = pending)),
            status.Subscribe(next => {
                // Read before taking the lock: it opens the pid file and the process it names.
                var probed = next is { State: AttachState.Unreachable, Reason: UnreachableReason };
                var pid = probed ? ProbeLivePid() : null;
                Update(() => {
                    if (next.State == AttachState.Connected) {
                        if (_outageSince is { } since) _outageEnded?.Invoke(_outageExpected, _time.GetUtcNow() - since);
                        _outageSince = null;
                        _outageReason = null;
                        _outageExpected = false;
                        _attachedPid = next.Identity?.Pid;
                    } else if (next.State == AttachState.Unreachable) {
                        _outageSince ??= _time.GetUtcNow();
                        _outageReason = next.Reason;
                    }
                    _raw = next;
                    if (probed) NoteLivePid(pid);
                });
            }));
    }

    /// The attach status the app shows: the service's own, relabelled while a restart is expected or the
    /// daemon is starting.
    public IObservable<AttachStatus> Status => _status.DistinctUntilChanged();

    /// Whether the daemon process is up but not yet serving its socket; replays on subscribe. True during an
    /// expected restart too, once the successor's process is up.
    public IObservable<bool> Starting => _starting.DistinctUntilChanged();

    /// Whether an outage right now is one the daemon chose, or one it is coming back from by itself.
    public bool Expected {
        get {
            lock (_lock) return IsExpected() || IsStarting();
        }
    }

    bool InDaemonOutage() =>
        _raw is { State: AttachState.Unreachable or AttachState.Connecting } && _outageReason == UnreachableReason;

    bool IsExpected() =>
        InDaemonOutage() && _outageSince is { } since && _time.GetUtcNow() - since < Grace
        && (_marker || _appJustUpdated());

    bool IsStarting() =>
        InDaemonOutage() && _startingSince is { } since && _time.GetUtcNow() - since < Grace;

    // The daemon the app was attached to can still be alive while it stops; only another process is starting.
    void NoteLivePid(int? pid) =>
        _startingSince = pid is { } live && live != _attachedPid ? _startingSince ?? _time.GetUtcNow() : null;

    int? ProbeLivePid() {
        if (_livePid is not { } probe) return null;
        try {
            return probe();
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap: reading the daemon's pid file failed: {ex.Message}");
            return null;
        }
    }

    // Published under the lock, so two sources updating at once cannot reach subscribers in the reverse order.
    void Update(Action change) {
        lock (_lock) {
            if (_disposed) return;
            change();
            if (_raw is null) return;
            if (!InDaemonOutage()) _startingSince = null;
            var expected = IsExpected();
            var starting = IsStarting();
            _outageExpected |= expected;
            // A daemon outage is polled while a probe exists, so a process that comes up is seen without waiting
            // on the connect backoff.
            var polling = expected || (InDaemonOutage() && _livePid is not null);
            _timer.Change(polling ? RetryInterval : Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _status.OnNext(
                expected ? new AttachStatus(AttachState.Unreachable, Reason, null, _raw.DaemonVersion)
                : starting ? new AttachStatus(AttachState.Unreachable, StartingReason, null, _raw.DaemonVersion)
                : _raw);
            _starting.OnNext(starting);
        }
    }

    // Probes and retries only from Unreachable: a retry cancels the attach in flight, and a daemon that is still
    // handshaking must be left to finish. The retry's own Unreachable re-arms the timer.
    void Tick() {
        bool probe;
        lock (_lock) {
            if (_disposed) return;
            probe = InDaemonOutage() && _raw?.State == AttachState.Unreachable;
        }
        var pid = probe ? ProbeLivePid() : null;
        var retry = false;
        Update(() => {
            if (probe) NoteLivePid(pid);
            retry = (IsExpected() || IsStarting()) && _raw?.State == AttachState.Unreachable;
        });
        if (retry) _ = ReattachAsync();
    }

    async Task ReattachAsync() {
        try {
            await _reattach().ConfigureAwait(false);
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap: reattach during daemon restart failed: {ex.Message}");
        }
    }

    public void Dispose() {
        lock (_lock) {
            if (_disposed) return;
            _disposed = true;
        }
        _subscription.Dispose();
        _timer.Dispose();
        _status.OnCompleted();
        _status.Dispose();
        _starting.OnCompleted();
        _starting.Dispose();
    }
}
