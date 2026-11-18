using Capacitor.Cli.Ide;
using Capacitor.Client;

namespace Capacitor.Cli.Tests.Unit.Ide;

/// <summary>What a client's retry restarts, given the daemon attach's and the server connection's current states.</summary>
public class HostReconnectTests {
    sealed class Errors : StringWriter {
        readonly TaskCompletionSource _written = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Written => _written.Task;

        public override void WriteLine(string? value) {
            base.WriteLine(value);
            _written.TrySetResult();
        }

        public override Task WriteLineAsync(string? value) {
            WriteLine(value);

            return Task.CompletedTask;
        }
    }

    sealed class Fixture {
        int _daemonRestarts, _laneRestarts;

        public FakeDaemonClientService Daemon { get; } = new();
        public FakeServerLane Lane { get; } = new();
        public Errors Errors { get; } = new();
        public Func<Task> RestartLane { get; set; } = () => Task.CompletedTask;

        public int DaemonRestarts => Volatile.Read(ref _daemonRestarts);
        public int LaneRestarts => Volatile.Read(ref _laneRestarts);

        public HostReconnect Build() => new(
            Daemon.Status, () => { Interlocked.Increment(ref _daemonRestarts); return Task.CompletedTask; },
            Lane.Status, () => { Interlocked.Increment(ref _laneRestarts); return RestartLane(); },
            Errors);

        public Fixture With(AttachState daemon, ServerLaneState lane) {
            Daemon.StatusSubject.OnNext(new(daemon, null, null));
            Lane.StatusSubject.OnNext(new(lane));

            return this;
        }
    }

    [Test]
    public async Task A_healthy_host_restarts_nothing() {
        var fixture = new Fixture().With(AttachState.Connected, ServerLaneState.Connected);

        await fixture.Build().ReconnectAsync().WithinDeadlineAsync("the reconnect call");

        await Assert.That(fixture.DaemonRestarts).IsEqualTo(0);
        await Assert.That(fixture.LaneRestarts).IsEqualTo(0);
    }

    [Test]
    public async Task An_unreachable_daemon_is_restarted_and_the_lane_left_alone() {
        var fixture = new Fixture().With(AttachState.Unreachable, ServerLaneState.Connected);

        await fixture.Build().ReconnectAsync().WithinDeadlineAsync("the reconnect call");

        await Assert.That(fixture.DaemonRestarts).IsEqualTo(1);
        await Assert.That(fixture.LaneRestarts).IsEqualTo(0);
    }

    [Test]
    public async Task A_signed_out_lane_is_restarted_and_the_daemon_left_alone() {
        var fixture = new Fixture().With(AttachState.Connected, ServerLaneState.SignedOut);

        await fixture.Build().ReconnectAsync().WithinDeadlineAsync("the reconnect call");

        await Assert.That(fixture.DaemonRestarts).IsEqualTo(0);
        await Assert.That(fixture.LaneRestarts).IsEqualTo(1);
    }

    [Test]
    public async Task A_retrying_lane_is_restarted() {
        var fixture = new Fixture().With(AttachState.Connected, ServerLaneState.Retrying);

        await fixture.Build().ReconnectAsync().WithinDeadlineAsync("the reconnect call");

        await Assert.That(fixture.LaneRestarts).IsEqualTo(1);
    }

    [Test]
    public async Task A_restart_that_fails_is_reported_and_does_not_throw() {
        var fixture = new Fixture().With(AttachState.Connected, ServerLaneState.SignedOut);
        fixture.RestartLane = () => throw new InvalidOperationException("negotiate refused");

        await fixture.Build().ReconnectAsync().WithinDeadlineAsync("the reconnect call");
        await fixture.Errors.Written.WithinDeadlineAsync("the failure to be written");

        await Assert.That(fixture.LaneRestarts).IsEqualTo(1);
        await Assert.That(fixture.Errors.ToString()).Contains("negotiate refused");
    }

    [Test]
    public async Task A_restart_still_running_does_not_block_the_caller() {
        var fixture = new Fixture().With(AttachState.Connected, ServerLaneState.Retrying);
        var never = new TaskCompletionSource();
        fixture.RestartLane = () => never.Task;

        await fixture.Build().ReconnectAsync().WithinDeadlineAsync("the reconnect call");

        await Assert.That(fixture.LaneRestarts).IsEqualTo(1);
        await Assert.That(never.Task.IsCompleted).IsFalse();
    }
}
