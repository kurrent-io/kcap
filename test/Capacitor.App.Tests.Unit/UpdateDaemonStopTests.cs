using System.Globalization;
using System.Reactive.Subjects;
using Capacitor.App.Services;
using Capacitor.App.Services.Mutation;
using Capacitor.App.Services.Update;
using Capacitor.Cli.Core.LocalIpc;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.App.Tests.Unit;

public class UpdateDaemonStopTests {
    [TempDaemonPaths] public required TempDaemonStore Daemons { get; init; }

    static readonly TimeSpan Bounded = TimeSpan.FromSeconds(5);

    sealed class NoObservation : IDaemonObservation {
        public Task<ObservedEvidence?> ObserveAsync(MutationRequest request, CancellationToken ct) => Task.FromResult<ObservedEvidence?>(null);
    }

    DaemonMutationLane Lane(FakeKcapCli? cli = null) =>
        new(Daemons.Store, new FakeLoginShellProbe(), new OutcomeChannel(), () => "/opt/kcap/bin/kcap",
            (_, path) => cli ?? new FakeKcapCli { CliPath = path }, _ => new NoObservation(), TimeProvider.System) {
            Classify = (_, _, _, _, _, _, _) => Task.FromResult<MutationOutcome>(new MutationOutcome.Succeeded()),
        };

    static ExpectedDaemonRestart Expected(BehaviorSubject<AttachStatus> status) =>
        new(status, new BehaviorSubject<bool>(false), () => false, () => Task.CompletedTask, TimeProvider.System);

    static readonly AttachStatus Down = new(AttachState.Unreachable, "daemon_unreachable", null);

    static LocalControlOpsException Unreachable() => new("daemon_unreachable", "connection refused");

    /// <summary>Velopack leaves a macOS or Linux daemon running, and it restarts at a turn boundary on its own.</summary>
    [Test]
    public async Task Only_Windows_stops_the_daemon_before_the_updater() {
        await using var lane = Lane();
        using var expected = Expected(new BehaviorSubject<AttachStatus>(Down));

        await Assert.That(UpdateDaemonStop.ForPlatform(false, lane, Daemons.Store, "daemon-a", expected, TimeProvider.System)).IsNull();
        await Assert.That(UpdateDaemonStop.ForPlatform(true, lane, Daemons.Store, "daemon-a", expected, TimeProvider.System)).IsNotNull();
    }

    [Test]
    public async Task A_daemon_that_does_not_answer_is_left_for_the_updater_to_end() {
        await using var lane = Lane();
        using var expected = Expected(new BehaviorSubject<AttachStatus>(Down));
        var stop = new UpdateDaemonStop(lane, "daemon-a", expected, TimeProvider.System, () => 4242,
            (_, _) => throw Unreachable());

        await Assert.That(await stop.StopAsync(_ => { }, CancellationToken.None).WaitAsync(Bounded)).IsEqualTo(DaemonHandoff.Interrupted);
    }

    [Test]
    public async Task No_daemon_process_and_no_socket_is_a_daemon_not_running() {
        await using var lane = Lane();
        using var expected = Expected(new BehaviorSubject<AttachStatus>(Down));
        var stop = new UpdateDaemonStop(lane, "daemon-a", expected, TimeProvider.System, () => null,
            (_, _) => throw Unreachable());

        await Assert.That(await stop.StopAsync(_ => { }, CancellationToken.None).WaitAsync(Bounded)).IsEqualTo(DaemonHandoff.NotRunning);
    }

    /// <summary>The real request against a socket nobody listens on, so the fallback does not hang on a missing
    /// daemon. The pid file names this test process, which is alive.</summary>
    [Test]
    public async Task A_live_pid_with_no_socket_falls_back_without_waiting() {
        Daemons.CreateFile(Path.GetRelativePath(Daemons.Directory, Daemons.Store.PidPath("daemon-a")),
            Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + "\n");
        await using var lane = Lane();
        using var expected = Expected(new BehaviorSubject<AttachStatus>(Down));
        var stop = UpdateDaemonStop.ForPlatform(true, lane, Daemons.Store, "daemon-a", expected, TimeProvider.System)!;

        await Assert.That(await stop.StopAsync(_ => { }, CancellationToken.None).WaitAsync(Bounded)).IsEqualTo(DaemonHandoff.Interrupted);
    }

    [Test]
    public async Task The_stop_counts_once_the_daemon_process_is_gone() {
        await using var lane = Lane();
        using var expected = Expected(new BehaviorSubject<AttachStatus>(Down));
        var reads = 0;
        TimeSpan? told = null;
        var stop = new UpdateDaemonStop(lane, "daemon-a", expected, TimeProvider.System,
            () => Interlocked.Increment(ref reads) < 4 ? 4242 : null,
            (waiting, _) => { waiting(TimeSpan.FromMinutes(5)); return Task.CompletedTask; });

        var handoff = await stop.StopAsync(wait => told = wait, CancellationToken.None).WaitAsync(Bounded);

        await Assert.That(handoff).IsEqualTo(DaemonHandoff.Stopped);
        await Assert.That(told).IsEqualTo(TimeSpan.FromMinutes(5));
    }

    /// <summary>Classifying a failure as success is the cardinal sin: an ack whose process never exits is no stop.</summary>
    [Test]
    public async Task A_daemon_that_acks_but_never_exits_is_not_called_stopped() {
        var time = new FakeTimeProvider();
        await using var lane = Lane();
        using var expected = Expected(new BehaviorSubject<AttachStatus>(Down));
        var stop = new UpdateDaemonStop(lane, "daemon-a", expected, time, () => 4242, (_, _) => Task.CompletedTask);

        var stopping = stop.StopAsync(_ => { }, CancellationToken.None);
        for (var i = 0; i < 200 && !stopping.IsCompleted; i++) {
            time.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(5);
        }

        await Assert.That(await stopping.WaitAsync(Bounded)).IsEqualTo(DaemonHandoff.Interrupted);
    }

    /// <summary>One lane for every daemon mutation: the stop waits behind a start already running.</summary>
    [Test]
    public async Task The_stop_runs_in_the_mutation_lane_after_the_mutation_ahead_of_it() {
        var gate = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var lane = Lane(new FakeKcapCli { VersionBehavior = _ => gate.Task });
        using var expected = Expected(new BehaviorSubject<AttachStatus>(Down));
        var asked = 0;
        var stop = new UpdateDaemonStop(lane, "daemon-a", expected, TimeProvider.System, () => null,
            (_, _) => { Interlocked.Increment(ref asked); throw Unreachable(); });

        var start = lane.RunAsync(new MutationRequest(MutationVerb.StartVerified, "default", "https://cap.example.test", "daemon-a"), CancellationToken.None);
        var stopping = stop.StopAsync(_ => { }, CancellationToken.None);
        await Task.Delay(100);
        await Assert.That(Volatile.Read(ref asked)).IsEqualTo(0);

        gate.SetResult("9.9.9");
        await start.WaitAsync(Bounded);

        await Assert.That(await stopping.WaitAsync(Bounded)).IsEqualTo(DaemonHandoff.NotRunning);
        await Assert.That(Volatile.Read(ref asked)).IsEqualTo(1);
    }

    /// <summary>Putting the update off closes the connection the daemon holds the stop on.</summary>
    [Test]
    public async Task Cancelling_withdraws_the_request_the_daemon_is_holding() {
        await using var lane = Lane();
        using var expected = Expected(new BehaviorSubject<AttachStatus>(Down));
        var withdrawn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stop = new UpdateDaemonStop(lane, "daemon-a", expected, TimeProvider.System, () => 4242, async (_, ct) => {
            try {
                await Task.Delay(Timeout.Infinite, ct);
            } finally {
                withdrawn.TrySetResult();
            }
        });
        using var cts = new CancellationTokenSource();

        var stopping = stop.StopAsync(_ => { }, cts.Token);
        await cts.CancelAsync();

        await Assert.That(async () => await stopping.WaitAsync(Bounded)).Throws<OperationCanceledException>();
        await withdrawn.Task.WaitAsync(Bounded);
    }

    /// <summary>A daemon the update stopped is not started again from the install being replaced, however long the
    /// app takes to exit; giving the update up hands it back.</summary>
    [Test]
    public async Task The_daemon_reads_as_restarting_from_the_stop_until_it_is_released() {
        await using var lane = Lane();
        var status = new BehaviorSubject<AttachStatus>(new AttachStatus(AttachState.Connected, null, []));
        var time = new FakeTimeProvider();
        using var expected = new ExpectedDaemonRestart(status, new BehaviorSubject<bool>(false), () => false, () => Task.CompletedTask, time);
        var stop = new UpdateDaemonStop(lane, "daemon-a", expected, time, () => null, (_, _) => throw Unreachable());

        await stop.StopAsync(_ => { }, CancellationToken.None).WaitAsync(Bounded);
        status.OnNext(Down);
        time.Advance(ExpectedDaemonRestart.Grace + TimeSpan.FromMinutes(1));
        await Assert.That(expected.Expected).IsTrue();

        stop.Release();
        await Assert.That(expected.Expected).IsFalse();
    }
}
