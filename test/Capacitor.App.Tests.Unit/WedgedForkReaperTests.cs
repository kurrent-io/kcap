using System.Diagnostics;
using System.Runtime.Versioning;
using TUnit.Core.Enums;
using Capacitor.App.Platform.Mac;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.App.Tests.Unit;

public class WedgedForkReaperTests {
    const string Self = "/Applications/kcap.app/Contents/MacOS/kcap-desktop";

    sealed class FakeProbe : IChildProcessProbe {
        public string? SelfImage { get; set; } = Self;
        public Dictionary<int, (string? Image, string? Identity)> Live { get; } = [];
        public List<int> Killed { get; } = [];
        /// Runs before the image read with this 1-based index.
        public (int Look, Action Change)? AtLook { get; set; }
        int _looks;

        public IReadOnlyList<int> Children() => Live.Keys.ToList();

        public string? ImagePath(int pid) {
            if (++_looks == AtLook?.Look) AtLook.Value.Change();
            return Live.TryGetValue(pid, out var c) ? c.Image : null;
        }

        public string? Identity(int pid) => Live.TryGetValue(pid, out var c) ? c.Identity : null;

        public void Kill(int pid) => Killed.Add(pid);
    }

    readonly FakeTimeProvider _time = new();
    readonly List<string> _reports = [];

    WedgedForkReaper Reaper(FakeProbe probe) => new(probe, _time, _reports.Add);

    [Test]
    public async Task Kills_a_copy_of_the_app_once_it_outlives_the_grace_period() {
        var probe = new FakeProbe { Live = { [42] = (Self, "id-42") } };
        var reaper = Reaper(probe);

        reaper.Sweep();
        _time.Advance(WedgedForkReaper.Grace - TimeSpan.FromMilliseconds(1));
        await Assert.That(reaper.Sweep()).IsEqualTo(0);

        _time.Advance(TimeSpan.FromMilliseconds(1));
        await Assert.That(reaper.Sweep()).IsEqualTo(1);
        await Assert.That(probe.Killed).IsEquivalentTo([42]);
        await Assert.That(_reports.Single()).Contains("42");
    }

    [Test]
    public async Task Leaves_children_running_another_executable() {
        var probe = new FakeProbe { Live = { [7] = ("/usr/bin/gh", "id-7") } };
        var reaper = Reaper(probe);

        reaper.Sweep();
        _time.Advance(WedgedForkReaper.Grace * 3);
        reaper.Sweep();

        await Assert.That(probe.Killed).IsEmpty();
    }

    [Test]
    public async Task Spares_a_child_that_execs_between_the_check_and_the_signal() {
        var probe = new FakeProbe { Live = { [9] = (Self, "id-9") } };
        // Look 1 is the first sweep, look 2 the second sweep's check, look 3 its re-check.
        probe.AtLook = (3, () => probe.Live[9] = ("/opt/homebrew/bin/kcap", "id-9"));
        var reaper = Reaper(probe);
        reaper.Sweep();
        _time.Advance(WedgedForkReaper.Grace);

        reaper.Sweep();

        await Assert.That(probe.Killed).IsEmpty();
    }

    /// A new incarnation behind a reused pid starts its own grace period instead of inheriting the old one's.
    [Test]
    public async Task Times_a_reused_pid_from_its_own_first_sighting() {
        var probe = new FakeProbe { Live = { [5] = (Self, "first") } };
        var reaper = Reaper(probe);
        reaper.Sweep();
        _time.Advance(WedgedForkReaper.Grace);

        probe.Live[5] = (Self, "second");
        reaper.Sweep();

        await Assert.That(probe.Killed).IsEmpty();
    }

    [Test]
    public async Task Forgets_a_child_that_left_so_its_return_restarts_the_clock() {
        var probe = new FakeProbe { Live = { [3] = (Self, "id-3") } };
        var reaper = Reaper(probe);
        reaper.Sweep();

        probe.Live.Clear();
        _time.Advance(WedgedForkReaper.Grace);
        reaper.Sweep();

        probe.Live[3] = (Self, "id-3");
        reaper.Sweep();

        await Assert.That(probe.Killed).IsEmpty();
    }

    [Test]
    public async Task Does_nothing_when_its_own_image_is_unreadable() {
        var probe = new FakeProbe { SelfImage = null, Live = { [4] = (null, "id-4") } };
        var reaper = Reaper(probe);
        reaper.Sweep();
        _time.Advance(WedgedForkReaper.Grace);

        await Assert.That(reaper.Sweep()).IsEqualTo(0);
    }

    /// Pins the libproc reads against the kernel: a real exec'd child is listed under its own
    /// image, never this process's, so the reaper leaves it alone.
    [Test, RunOn(OS.MacOs)]
    [SupportedOSPlatform("macos")]
    public async Task Libproc_probe_sees_a_real_child_under_its_execed_image() {
        var probe = new LibprocChildProcessProbe();
        using var child = Process.Start(new ProcessStartInfo("/bin/sleep", ["30"]) { UseShellExecute = false })!;
        try {
            await Assert.That(probe.SelfImage).IsNotNull();
            await Assert.That(probe.Children()).Contains(child.Id);
            await Assert.That(probe.ImagePath(child.Id)).IsEqualTo("/bin/sleep");
            await Assert.That(probe.Identity(child.Id)).IsNotNull();

            var reaper = new WedgedForkReaper(probe, _time, _reports.Add);
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
