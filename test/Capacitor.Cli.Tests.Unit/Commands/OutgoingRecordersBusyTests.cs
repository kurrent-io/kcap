using Capacitor.Cli.Commands;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>A daemon still starting answers its control socket only once it has started, so an uninstall waits a
/// bounded time for it and reports one that stays silent as not answering, never as running agents.</summary>
public class OutgoingRecordersBusyTests {
    readonly FakeTimeProvider _time = new();
    readonly List<string> _said = [];

    /// <summary>Advances the fake clock until the probe loop finishes; a delay registered after an advance just waits
    /// for the next one.</summary>
    async Task<IReadOnlyList<DaemonBusy>> RunAsync(IEnumerable<string> names, Func<string, Task<DaemonBusyReason?>> probe,
        TimeSpan? wait = null) {
        var busy = OutgoingRecorders.BusyAsync(names, probe, _time, _said.Add, wait ?? OutgoingRecorders.StartingWait);

        for (var i = 0; i < 1000 && !busy.IsCompleted; i++) {
            await Task.Delay(1);
            _time.Advance(TimeSpan.FromSeconds(1));
        }

        return await busy;
    }

    [Test]
    public async Task A_daemon_that_answers_idle_after_starting_holds_nothing_up() {
        var probes = 0;

        var busy = await RunAsync(["me"], _ => Task.FromResult(++probes <= 2 ? DaemonBusyReason.NoAnswer : (DaemonBusyReason?)null));

        await Assert.That(busy).IsEmpty();
        await Assert.That(probes).IsEqualTo(3);
        await Assert.That(_said).IsEquivalentTo(["Waiting for daemon 'me' to finish starting…"]);
    }

    [Test]
    public async Task A_daemon_silent_to_the_bound_is_reported_as_not_answering() {
        var started = _time.GetTimestamp();

        var busy = await RunAsync(["me"], _ => Task.FromResult<DaemonBusyReason?>(DaemonBusyReason.NoAnswer));

        await Assert.That(busy).IsEquivalentTo([new DaemonBusy("me", DaemonBusyReason.NoAnswer)]);
        await Assert.That(_time.GetElapsedTime(started)).IsGreaterThanOrEqualTo(OutgoingRecorders.StartingWait);
        await Assert.That(busy[0].Line).IsEqualTo("daemon 'me' did not answer in time; it may still be starting");
        await Assert.That(DaemonBusy.AnyMayRunAgents(busy)).IsFalse();
    }

    [Test]
    public async Task A_daemon_that_answers_at_once_is_not_waited_for() {
        var busy = await OutgoingRecorders.BusyAsync(
            ["idle", "working", "old"],
            name => Task.FromResult<DaemonBusyReason?>(name switch {
                "working" => DaemonBusyReason.Running,
                "old"     => DaemonBusyReason.Unknown,
                _         => null,
            }),
            _time, _said.Add, OutgoingRecorders.StartingWait);

        await Assert.That(busy).IsEquivalentTo([
            new DaemonBusy("working", DaemonBusyReason.Running),
            new DaemonBusy("old", DaemonBusyReason.Unknown),
        ]);
        await Assert.That(_said).IsEmpty();
        await Assert.That(DaemonBusy.AnyMayRunAgents(busy)).IsTrue();
    }

    /// <summary>The wait is shared: a second silent daemon does not get a bound of its own.</summary>
    [Test]
    public async Task Silent_daemons_share_one_bound() {
        var started = _time.GetTimestamp();

        var busy = await RunAsync(["a", "b"], _ => Task.FromResult<DaemonBusyReason?>(DaemonBusyReason.NoAnswer));

        await Assert.That(busy.Select(b => b.Reason)).IsEquivalentTo([DaemonBusyReason.NoAnswer, DaemonBusyReason.NoAnswer]);
        await Assert.That(_time.GetElapsedTime(started)).IsLessThan(OutgoingRecorders.StartingWait * 2);
        await Assert.That(_said).IsEquivalentTo(["Waiting for daemon 'a' to finish starting…"]);
    }

    [Test]
    public async Task A_shorter_wait_bounds_a_silent_daemon() {
        var started = _time.GetTimestamp();
        var wait    = TimeSpan.FromSeconds(8);

        var busy = await RunAsync(["me"], _ => Task.FromResult<DaemonBusyReason?>(DaemonBusyReason.NoAnswer), wait);

        await Assert.That(busy).IsEquivalentTo([new DaemonBusy("me", DaemonBusyReason.NoAnswer)]);
        await Assert.That(_time.GetElapsedTime(started)).IsLessThan(OutgoingRecorders.StartingWait);
    }
}
