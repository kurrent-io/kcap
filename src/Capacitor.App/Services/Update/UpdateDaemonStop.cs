using Capacitor.App.Services.Mutation;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.App.Services.Update;

/// <summary>
/// Velopack's updater on Windows ends every process running from the install folder, the daemon among them. Before it
/// takes over, this stops the daemon the way a self-update restarts it elsewhere: once its interactive agents reach a
/// turn boundary, or its own turn wait runs out, keeping them for the next daemon to resume. The stop runs in the
/// mutation lane, and the app's recovery is held off from the moment it is asked for: an ensure that started the
/// daemon again would run the install being replaced, and the updater would end it mid-resume.
/// </summary>
public sealed class UpdateDaemonStop : IUpdateDaemonStop {
    internal const string NotRunningReason = "daemon_not_running";

    /// <summary>The daemon's own stop fits in about 18 s once it is released; the CLI's stop waits as long.</summary>
    internal static readonly TimeSpan ExitWait = TimeSpan.FromSeconds(30);
    static readonly TimeSpan ExitPoll = TimeSpan.FromMilliseconds(200);

    readonly DaemonMutationLane _lane;
    readonly string _daemonName;
    readonly ExpectedDaemonRestart _expected;
    readonly TimeProvider _time;
    readonly Func<int?> _livePid;
    readonly Func<Action<TimeSpan>, CancellationToken, Task> _requestStop;
    readonly Lock _gate = new();
    IDisposable? _hold;

    /// <summary>Null off Windows, where the updater leaves the daemon running and it restarts at a turn boundary by
    /// itself once it sees its new binary.</summary>
    public static IUpdateDaemonStop? ForPlatform(
            bool windows, DaemonMutationLane lane, DaemonStore store, string daemonName, ExpectedDaemonRestart expected,
            TimeProvider time) =>
        windows ? new UpdateDaemonStop(lane, daemonName, expected, time, () => DaemonPidLiveness.LivePid(store, daemonName),
            (waiting, ct) => new LocalControlOps(store, daemonName, time).StopAtTurnBoundaryAsync(waiting, ct)) : null;

    internal UpdateDaemonStop(
            DaemonMutationLane lane, string daemonName, ExpectedDaemonRestart expected, TimeProvider time,
            Func<int?> livePid, Func<Action<TimeSpan>, CancellationToken, Task> requestStop) {
        _lane = lane;
        _daemonName = daemonName;
        _expected = expected;
        _time = time;
        _livePid = livePid;
        _requestStop = requestStop;
    }

    public async Task<DaemonHandoff> StopAsync(Action<TimeSpan> waiting, CancellationToken ct) {
        lock (_gate) _hold ??= _expected.HoldForUpdate();
        var request = new MutationRequest(MutationVerb.StopForUpdate, "", "", _daemonName, RequestId: Guid.NewGuid().ToString("N"));
        var outcome = await _lane.RunExclusiveAsync(request, token => StopInLaneAsync(waiting, token), ct).ConfigureAwait(false);
        return outcome switch {
            MutationOutcome.Succeeded => DaemonHandoff.Stopped,
            MutationOutcome.Refused { Reason: NotRunningReason } => DaemonHandoff.NotRunning,
            _ => DaemonHandoff.Interrupted,
        };
    }

    public void Release() {
        IDisposable? hold;
        lock (_gate) {
            hold = _hold;
            _hold = null;
        }
        hold?.Dispose();
    }

    /// <summary>The ack alone is never the evidence: only the process the pid file named being gone is.</summary>
    async Task<MutationOutcome> StopInLaneAsync(Action<TimeSpan> waiting, CancellationToken ct) {
        var pid = _livePid();
        try {
            await _requestStop(waiting, ct).ConfigureAwait(false);
        } catch (LocalControlOpsException ex) {
            if (pid is null && _livePid() is null) return new MutationOutcome.Refused(NotRunningReason, RecoverySurface.Attention);
            Console.Error.WriteLine($"kcap: the daemon did not stop for the update ({ex.Reason}): {ex.Message}");
            return new MutationOutcome.Failed(DaemonMutationLane.UnexpectedExitCode, ex.Reason, RecoverySurface.Attention);
        }

        if (pid is null) return new MutationOutcome.Unconfirmed("pid_unknown");
        var deadline = _time.GetUtcNow() + ExitWait;
        while (_livePid() == pid) {
            if (_time.GetUtcNow() >= deadline) return new MutationOutcome.Unconfirmed("still_running");
            await Task.Delay(ExitPoll, _time, ct).ConfigureAwait(false);
        }
        return new MutationOutcome.Succeeded();
    }
}
