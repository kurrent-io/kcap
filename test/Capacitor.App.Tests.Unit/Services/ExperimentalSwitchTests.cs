using System.Collections.Concurrent;
using System.Reactive.Concurrency;
using System.Reactive.Subjects;
using Capacitor.App.Services;
using Capacitor.Cli.Core.Config;

namespace Capacitor.App.Tests.Unit.Services;

/// The switch reads the shared store each time, so a change made elsewhere applies at the next ask.
public class ExperimentalSwitchTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    static readonly ExperimentalFeature Feature = ExperimentalFeatures.InAppPages;
    static readonly string Id = Feature.Id;

    [Test]
    public async Task A_profile_that_never_set_it_reads_off() =>
        await Assert.That(new ExperimentalSwitch(new ExperimentalSettingsStore(Config.Root), "work", Feature).IsOn).IsFalse();

    [Test]
    public async Task A_change_to_the_store_reads_back_at_the_next_ask() {
        var store = new ExperimentalSettingsStore(Config.Root);
        var sut = new ExperimentalSwitch(store, "work", Feature);

        await store.SetAsync("work", Id, true, CancellationToken.None);
        await Assert.That(sut.IsOn).IsTrue();

        await store.SetAsync("work", Id, false, CancellationToken.None);
        await Assert.That(sut.IsOn).IsFalse();
    }

    [Test]
    public async Task Another_profiles_switch_and_no_profile_read_off() {
        var store = new ExperimentalSettingsStore(Config.Root);
        await store.SetAsync("other", Id, true, CancellationToken.None);

        await Assert.That(new ExperimentalSwitch(store, "work", Feature).IsOn).IsFalse();
        await Assert.That(new ExperimentalSwitch(store, null, Feature).IsOn).IsFalse();
    }

    [Test]
    public async Task An_unreadable_store_reads_off() {
        var store = new ExperimentalSettingsStore(Config.Root);
        await File.WriteAllTextAsync(store.Path, "{ not json");

        await Assert.That(new ExperimentalSwitch(store, "work", Feature).IsOn).IsFalse();
    }

    /// A focus or a Settings save re-reads the switch on the worker, never on the caller's thread,
    /// and only a reading that differs from the last one signals.
    [Test]
    public async Task Flips_read_on_the_worker_and_signal_only_a_change() {
        using var worker = new EventLoopScheduler(start => new Thread(start) { Name = "switch-worker", IsBackground = true });
        var on = false;
        var threads = new ConcurrentBag<string?>();
        var mayChange = new Subject<System.Reactive.Unit>();
        var flips = 0;
        using var subscription = ExperimentalSwitch.Flips(() => {
            threads.Add(Thread.CurrentThread.Name);
            return Volatile.Read(ref on);
        }, mayChange, worker).Subscribe(_ => Interlocked.Increment(ref flips));

        mayChange.OnNext(System.Reactive.Unit.Default);
        await WorkspaceFixtures.WaitUntilAsync(() => threads.Count == 2, what: "two unchanged reads");
        Volatile.Write(ref on, true);
        mayChange.OnNext(System.Reactive.Unit.Default);
        mayChange.OnNext(System.Reactive.Unit.Default);
        await WorkspaceFixtures.WaitUntilAsync(() => threads.Count == 4 && Volatile.Read(ref flips) > 0, what: "four reads and the flip");
        // The last read is processed on the worker; a second flip would follow it there.
        var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        worker.Schedule(drained.SetResult);
        await drained.Task;

        await Assert.That(Volatile.Read(ref flips)).IsEqualTo(1);
        await Assert.That(threads.All(name => name == "switch-worker")).IsTrue();
    }
}
