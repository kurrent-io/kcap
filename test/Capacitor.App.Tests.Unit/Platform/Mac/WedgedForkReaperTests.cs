using System.Diagnostics;
using System.Runtime.Versioning;
using TUnit.Core.Enums;
using Capacitor.App.Platform.Mac;

namespace Capacitor.App.Tests.Unit.Platform.Mac;

public class WedgedForkReaperTests {
    sealed class FakeProbe : IChildProcessProbe {
        public Dictionary<int, (bool? Execed, string? Identity)> Live { get; } = [];
        public List<int> Killed { get; } = [];
        /// Runs before the exec check with this 1-based index.
        public (int Look, Action Change)? AtLook { get; set; }
        int _looks;

        public IReadOnlyList<int> Children() => Live.Keys.ToList();

        public bool? HasExeced(int pid) {
            if (++_looks == AtLook?.Look) AtLook.Value.Change();
            return Live.TryGetValue(pid, out var c) ? c.Execed : null;
        }

        public string? Identity(int pid) => Live.TryGetValue(pid, out var c) ? c.Identity : null;

        public void Kill(int pid) => Killed.Add(pid);
    }

    /// Moves the monotonic timestamp and the wall clock independently.
    sealed class SplitClock : TimeProvider {
        long _ticks;
        public DateTimeOffset Wall { get; set; } = DateTimeOffset.UnixEpoch;
        public void Advance(TimeSpan by) => _ticks += by.Ticks;
        public override long GetTimestamp() => _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override DateTimeOffset GetUtcNow() => Wall;
    }

    /// The real probe, listing only <paramref name="pid"/>: the test host is shared, and with the clock past the grace
    /// period a sweep over all its children kills any concurrent test's child that has not reached exec yet.
    sealed class OnlyChild(IChildProcessProbe probe, int pid) : IChildProcessProbe {
        public IReadOnlyList<int> Children() => probe.Children().Contains(pid) ? [pid] : [];
        public bool? HasExeced(int p) => probe.HasExeced(p);
        public string? Identity(int p) => probe.Identity(p);
        public void Kill(int p) => probe.Kill(p);
    }

    readonly SplitClock _time = new();
    readonly List<string> _reports = [];

    WedgedForkReaper Reaper(FakeProbe probe) => new(probe, _time, _reports.Add);

    [Test]
    public async Task Kills_an_unexeced_fork_once_it_outlives_the_grace_period() {
        var probe = new FakeProbe { Live = { [42] = (false, "id-42") } };
        var reaper = Reaper(probe);

        reaper.Sweep();
        _time.Advance(WedgedForkReaper.Grace - TimeSpan.FromTicks(1));
        await Assert.That(reaper.Sweep()).IsEqualTo(0);

        _time.Advance(TimeSpan.FromTicks(1));
        await Assert.That(reaper.Sweep()).IsEqualTo(1);
        await Assert.That(probe.Killed).IsEquivalentTo([42]);
        await Assert.That(_reports.Single()).Contains("42");
    }

    /// Covers the app relaunching itself: that child runs the same executable, but it has execed.
    [Test]
    public async Task Leaves_execed_children_alone_whatever_they_run() {
        var probe = new FakeProbe { Live = { [7] = (true, "id-7") } };
        var reaper = Reaper(probe);

        reaper.Sweep();
        _time.Advance(WedgedForkReaper.Grace * 3);
        reaper.Sweep();

        await Assert.That(probe.Killed).IsEmpty();
    }

    [Test]
    public async Task Spares_a_child_that_execs_before_the_final_check() {
        var probe = new FakeProbe { Live = { [9] = (false, "id-9") } };
        // Look 1 is the first sweep, look 2 the second sweep's check, look 3 its re-check.
        probe.AtLook = (3, () => probe.Live[9] = (true, "id-9"));
        var reaper = Reaper(probe);
        reaper.Sweep();
        _time.Advance(WedgedForkReaper.Grace);

        reaper.Sweep();

        await Assert.That(probe.Killed).IsEmpty();
    }

    /// A new incarnation behind a reused pid starts its own grace period instead of inheriting the old one's.
    [Test]
    public async Task Times_a_reused_pid_from_its_own_first_sighting() {
        var probe = new FakeProbe { Live = { [5] = (false, "first") } };
        var reaper = Reaper(probe);
        reaper.Sweep();
        _time.Advance(WedgedForkReaper.Grace);

        probe.Live[5] = (false, "second");
        reaper.Sweep();

        await Assert.That(probe.Killed).IsEmpty();
    }

    [Test]
    public async Task Forgets_a_child_that_left_so_its_return_restarts_the_clock() {
        var probe = new FakeProbe { Live = { [3] = (false, "id-3") } };
        var reaper = Reaper(probe);
        reaper.Sweep();

        probe.Live.Clear();
        _time.Advance(WedgedForkReaper.Grace);
        reaper.Sweep();

        probe.Live[3] = (false, "id-3");
        reaper.Sweep();

        await Assert.That(probe.Killed).IsEmpty();
    }

    /// The kill lands before the runtime reaps the child, so the next sweeps can still list it.
    [Test]
    public async Task Signals_and_reports_a_child_once_while_it_awaits_reaping() {
        var probe = new FakeProbe { Live = { [8] = (false, "id-8") } };
        var reaper = Reaper(probe);
        reaper.Sweep();
        _time.Advance(WedgedForkReaper.Grace);

        reaper.Sweep();
        _time.Advance(WedgedForkReaper.Period);
        reaper.Sweep();

        await Assert.That(probe.Killed).IsEquivalentTo([8]);
        await Assert.That(_reports).Count().IsEqualTo(1);
    }

    [Test]
    public async Task Ignores_wall_clock_jumps() {
        var probe = new FakeProbe { Live = { [6] = (false, "id-6") } };
        var reaper = Reaper(probe);
        reaper.Sweep();

        _time.Wall += TimeSpan.FromHours(1);
        reaper.Sweep();

        await Assert.That(probe.Killed).IsEmpty();
    }

    /// Pins the libproc reads against the kernel: an execed child and this process both carry the exec flag.
    [Test, RunOn(OS.MacOs)]
    [SupportedOSPlatform("macos")]
    public async Task Libproc_probe_reads_the_exec_flag_of_a_real_child() {
        var probe = new LibprocChildProcessProbe();
        using var child = Process.Start(new ProcessStartInfo("/bin/sleep", ["30"]) { UseShellExecute = false })!;
        try {
            await Assert.That(probe.Children()).Contains(child.Id);
            await Assert.That(probe.HasExeced(child.Id)).IsTrue();
            await Assert.That(probe.HasExeced(Environment.ProcessId)).IsTrue();
            await Assert.That(probe.Identity(child.Id)).IsNotNull();

            var reaper = new WedgedForkReaper(new OnlyChild(probe, child.Id), _time, _reports.Add);
            reaper.Sweep();
            _time.Advance(WedgedForkReaper.Grace);
            await Assert.That(reaper.Sweep()).IsEqualTo(0);
            await Assert.That(child.HasExited).IsFalse();
        } finally {
            child.Kill();
            await child.WaitForExitAsync();
        }
    }
}
