using Capacitor.Cli.Core;
using Capacitor.Cli.Services;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Tests.Unit.Services;

public class ServiceTxnHolderTests {
    [TempDaemonPaths] public required TempDaemonStore Daemons { get; init; }

    /// <summary>The verbs and the switches that say what the holder is doing; a flag's value, which could be
    /// anything the user typed, never.</summary>
    [Test]
    [Arguments(new[] { "/opt/kcap/kcap", "daemon", "service", "start", "--name", "sandbox", "--verify" }, "kcap daemon service start --verify")]
    [Arguments(new[] { "/usr/local/bin/kcap.exe", "daemon", "service", "stop", "--keep-agents" }, "kcap daemon service stop --keep-agents")]
    [Arguments(new[] { "kcap", "setup", "--token", "secret" }, "kcap setup")]
    [Arguments(new[] { "kcap" }, "kcap")]
    public async Task The_command_names_verbs_and_switches_only(string[] commandLine, string expected) =>
        await Assert.That(ServiceTxnHolder.CommandOf(commandLine)).IsEqualTo(expected);

    [Test]
    public async Task A_record_reads_back_as_written() {
        var holder = new ServiceTxnHolder(4242, "tk:123", DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000), "kcap daemon service start");

        await Assert.That(ServiceTxnHolder.Parse(holder.Serialize())).IsEqualTo(holder);
    }

    /// <summary>A reader can catch the record half-written; that names nobody rather than a wrong pid.</summary>
    [Test]
    [Arguments("")]
    [Arguments("4242\ntk:1")]
    [Arguments("x\n\n1\nkcap")]
    [Arguments("4242\n\n1\n")]
    public async Task A_partial_record_names_nobody(string text) =>
        await Assert.That(ServiceTxnHolder.Parse(text)).IsNull();

    [Test]
    [Arguments(5, "`kcap daemon service start` (pid 7, for 5s)")]
    [Arguments(125, "`kcap daemon service start` (pid 7, for 2m 5s)")]
    public async Task The_description_says_how_long_it_has_held_the_lock(int seconds, string expected) {
        var since  = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        var holder = new ServiceTxnHolder(7, null, since, "kcap daemon service start");

        await Assert.That(holder.Describe(since.AddSeconds(seconds))).IsEqualTo(expected);
    }

    [Test]
    public async Task Taking_the_lock_names_this_process_until_it_is_released() {
        var time = new FakeTimeProvider(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000));

        using (var held = await ServiceTxnLock.TryAcquireAsync(Daemons.Store, "named", TimeSpan.Zero, time)) {
            await Assert.That(held).IsNotNull();

            var holder = ServiceTxnHolder.Read(Daemons.Store, "named");
            await Assert.That(holder).IsNotNull();
            await Assert.That(holder!.Pid).IsEqualTo(Environment.ProcessId);
            await Assert.That(holder.Since).IsEqualTo(time.GetUtcNow());
        }

        await Assert.That(File.Exists(Daemons.Store.ServiceLockHolderPath("named"))).IsFalse();
        await Assert.That(ServiceTxnHolder.Read(Daemons.Store, "named")).IsNull();
    }

    /// <summary>A record a crashed holder left behind names a process that is gone, so it names nobody.</summary>
    [Test]
    public async Task A_record_left_by_a_process_that_is_gone_names_nobody() {
        using var gone = DummyProcess.StartSleep(30);
        var token = ProcessStartToken.ForPid(gone.Pid);
        gone.Kill();
        gone.WaitForExit(TimeSpan.FromSeconds(5));
        Daemons.Store.EnsureDirectory();
        File.WriteAllText(Daemons.Store.ServiceLockHolderPath("left"),
            new ServiceTxnHolder(gone.Pid, token, DateTimeOffset.UtcNow, "kcap daemon service start").Serialize());

        await Assert.That(ServiceTxnHolder.Read(Daemons.Store, "left")).IsNull();
    }

    /// <summary>A verb a person typed says once who it is waiting for, then takes the lock when it is
    /// released; one that is never released gives up at the bound.</summary>
    [Test]
    public async Task A_waiting_acquire_says_who_holds_the_lock_and_takes_it_once_released() {
        var time = new FakeTimeProvider();
        var held = await ServiceTxnLock.TryAcquireAsync(Daemons.Store, "w", TimeSpan.Zero, TimeProvider.System);
        await Assert.That(held).IsNotNull();
        var told = new List<ServiceTxnHolder?>();

        var pending = ServiceTxnLock.AcquireWaitingAsync(Daemons.Store, "w", TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(2), time,
            holder => { lock (told) told.Add(holder); return Task.CompletedTask; });

        for (var steps = 0; Count() == 0; steps++) {
            if (steps > 2000) throw new TimeoutException("the quiet wait never ended under the fake clock");
            time.Advance(TimeSpan.FromMilliseconds(100));
            await Task.Delay(5);
        }

        await Assert.That(told.Single()!.Pid).IsEqualTo(Environment.ProcessId);
        await Assert.That(pending.IsCompleted).IsFalse();

        held!.Dispose();

        for (var steps = 0; !pending.IsCompleted; steps++) {
            if (steps > 2000) throw new TimeoutException("the released lock was never taken under the fake clock");
            time.Advance(TimeSpan.FromMilliseconds(100));
            await Task.Delay(5);
        }

        using var taken = await pending;
        await Assert.That(taken).IsNotNull();
        await Assert.That(Count()).IsEqualTo(1);

        int Count() { lock (told) return told.Count; }
    }

    [Test]
    public async Task A_waiting_acquire_gives_up_at_its_bound() {
        var time = new FakeTimeProvider();
        using var held = await ServiceTxnLock.TryAcquireAsync(Daemons.Store, "g", TimeSpan.Zero, TimeProvider.System);
        await Assert.That(held).IsNotNull();

        var pending = ServiceTxnLock.AcquireWaitingAsync(Daemons.Store, "g", TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(2), time,
            _ => Task.CompletedTask);

        for (var steps = 0; !pending.IsCompleted; steps++) {
            if (steps > 2000) throw new TimeoutException("the wait never gave up under the fake clock");
            time.Advance(TimeSpan.FromMilliseconds(500));
            await Task.Delay(5);
        }

        await Assert.That(await pending).IsNull();
    }
}
