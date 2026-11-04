using System.Reactive.Linq;
using System.Reactive.Subjects;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.App.Services.Update;

/// <summary>
/// After an update, deals with a daemon still connected on an older version. A daemon restarts itself when its binary
/// changes, but one that started while the update was replacing that binary can miss the change, and stays on the old
/// version until something restarts it: that daemon, with no restart queued, is asked once to restart at its agents'
/// next turn boundary. A daemon running a binary this app's update did not replace, such as an npm install, would come
/// back on the same version, so it is not asked; <see cref="OtherInstall"/> reports it instead, queued restart or not.
/// </summary>
public sealed class StaleDaemonNudge : IDisposable {
    /// Past the daemon's own 15 s binary poll, so one that noticed the change has queued its restart by then.
    internal static readonly TimeSpan Grace = TimeSpan.FromSeconds(20);

    /// The mode for a daemon without <see cref="RestartIpc.TurnBoundaryCapability"/>, which reads any mode it does not
    /// know as a restart that does not wait for turns.
    internal const string WhenIdleMode = "when-idle";

    readonly Lock _lock = new();
    readonly Func<int, string?> _executableOf;
    readonly Func<string, string, DaemonOnOtherInstall?> _otherInstallOf;
    readonly Func<string, CancellationToken, Task<string>> _request;
    readonly Action<string, string> _outcome;
    readonly TimeProvider _time;
    readonly CancellationToken _ct;
    readonly BehaviorSubject<DaemonOnOtherInstall?> _otherInstall = new(null);
    readonly IDisposable _subscription;
    ITimer? _timer;
    // A disposed timer can still run a callback it already queued: the generation tells a stale one to do nothing.
    long _generation;
    (AttachStatus Status, string? Version, int? Pid, bool Pending) _latest;
    bool _asked;
    bool _disposed;

    /// <param name="daemon">The connected daemon's version and process id.</param>
    /// <param name="executableOf">The binary a process runs; null when it cannot be read.</param>
    /// <param name="otherInstallOf">The report for a daemon of a version on a binary, or null when the binary is this app's CLI.</param>
    /// <param name="request">Sends a restart request in the given mode and returns the daemon's ack.</param>
    /// <param name="outcome">Told the mode sent and the ack, or why the request failed.</param>
    public StaleDaemonNudge(
            string appVersion, IObservable<AttachStatus> status, IObservable<(string? Version, int? Pid)> daemon, IObservable<bool> restartPending,
            Func<int, string?> executableOf, Func<string, string, DaemonOnOtherInstall?> otherInstallOf,
            Func<string, CancellationToken, Task<string>> request, Action<string, string> outcome, TimeProvider time, CancellationToken ct) {
        _executableOf = executableOf;
        _otherInstallOf = otherInstallOf;
        _request = request;
        _outcome = outcome;
        _time = time;
        _ct = ct;
        _subscription = status.CombineLatest(daemon, restartPending, (s, d, pending) => (Status: s, d.Version, d.Pid, Pending: pending))
            .Do(state => { lock (_lock) _latest = state; })
            .Select(state => (Stale: state.Status.State == AttachState.Connected && Behind(state.Version, appVersion), state.Pending))
            .DistinctUntilChanged()
            .Subscribe(Arm);
    }

    /// The daemon this run found on a binary its update did not replace, while it stays behind.
    public IObservable<DaemonOnOtherInstall?> OtherInstall => _otherInstall.DistinctUntilChanged();

    internal static bool Behind(string? daemonVersion, string appVersion) =>
        daemonVersion is { Length: > 0 } && PrereleaseSemver.IsNewer(Core(appVersion), Core(daemonVersion));

    static string Core(string version) => version.Split('+', 2)[0];

    /// A queued restart appearing or clearing re-arms too: the daemon's own poll queuing one ends the wait for a request.
    void Arm((bool Stale, bool Pending) state) {
        lock (_lock) {
            var generation = ++_generation;
            _timer?.Dispose();
            _timer = state.Stale && !_disposed
                ? _time.CreateTimer(_ => Fire(generation), null, Grace, Timeout.InfiniteTimeSpan)
                : null;
            if (!state.Stale && !_disposed) _otherInstall.OnNext(null);
        }
    }

    void Fire(long generation) {
        (AttachStatus Status, string? Version, int? Pid, bool Pending) latest;
        lock (_lock) {
            if (_disposed || generation != _generation) return;
            latest = _latest;
        }

        if (latest.Version is not { } version) return;
        var executable = latest.Pid is { } pid ? _executableOf(pid) : null;
        if (executable is not null && _otherInstallOf(version, executable) is { } other) {
            lock (_lock) {
                if (!_disposed && generation == _generation) _otherInstall.OnNext(other);
            }
            return;
        }

        lock (_lock) {
            if (_disposed || generation != _generation) return;
            _otherInstall.OnNext(null);
            if (_asked || latest.Pending) return;
            _asked = true;
        }
        var mode = latest.Status.Capabilities?.Contains(RestartIpc.TurnBoundaryCapability) == true ? RestartIpc.TurnBoundaryMode : WhenIdleMode;
        _ = SendAsync(mode);
    }

    async Task SendAsync(string mode) {
        string result;
        try {
            result = await _request(mode, _ct).ConfigureAwait(false);
        } catch (OperationCanceledException) when (_ct.IsCancellationRequested) {
            return;
        } catch (Exception ex) {
            result = ex is LocalControlOpsException ops ? ops.Reason : ex.GetType().Name;
        }
        _outcome(mode, result);
    }

    public void Dispose() {
        lock (_lock) {
            _disposed = true;
            _generation++;
            _timer?.Dispose();
            _timer = null;
        }
        _subscription.Dispose();
    }
}
