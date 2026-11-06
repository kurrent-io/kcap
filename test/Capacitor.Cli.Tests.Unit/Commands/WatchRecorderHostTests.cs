using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Recording.Harness.Muse;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class WatchRecorderHostTests {
    [TempHome] public required TempHome Home { get; init; }

    static readonly DateTimeOffset T0 = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);

    static Dictionary<string, int> Ready(params string[] keys) => keys.ToDictionary(k => k, _ => 1);

    /// <summary>A kill that fails leaves the child's watcher running, so the stop is confirmed only by
    /// the retry that ends it.</summary>
    [Test]
    public async Task A_child_is_confirmed_stopped_only_once_its_watcher_is_gone() {
        using var config = new TempConfigRoot();
        var paths = new WatcherPaths(config.PathTo("watchers"));
        Directory.CreateDirectory(paths.Directory);
        var http     = new FixedCapacitorHttpClient();
        var resolved = Resolutions.None(config.Root);
        var watchers = TestWatchers.In(paths, config.Root, resolved, http);
        var host     = new WatchRecorderHost(config.Root, resolved, TestHarnesses.Under(Home), http, watchers, TimeProvider.System, () => { }, () => { });

        using var watcher = DummyProcess.StartSleep(30);
        var       pidFile = paths.PidFile("s-a");
        await File.WriteAllTextAsync(pidFile, $"{watcher.Pid}\n{PidIdentity.Capture(watcher.Pid)}");

        var children = new MuseChildWatchers();
        children.Spawned("s-a", "/a/session.jsonl", T0);
        children.SelectStops(Ready("s-a"), T0);
        var now   = T0 + MuseChildWatchers.StopGrace;
        var ready = Ready("s-a");

        int failed;
        using (new FileStream(pidFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            failed = await children.Stop(host, children.SelectStops(ready, now), ready);

        await Assert.That(failed).IsEqualTo(0);
        await Assert.That(watcher.HasExited).IsFalse();
        await Assert.That(children.RecheckPending).IsTrue();

        var retried = await children.Stop(host, children.SelectStops(ready, now.AddSeconds(1)), ready);

        await Assert.That(retried).IsEqualTo(1);
        await Assert.That(watcher.WaitForExit(TimeSpan.FromSeconds(5))).IsTrue();
        await Assert.That(children.RecheckPending).IsFalse();
        await Assert.That(children.SelectStops(ready, now.AddSeconds(2))).IsEmpty();
    }
}
