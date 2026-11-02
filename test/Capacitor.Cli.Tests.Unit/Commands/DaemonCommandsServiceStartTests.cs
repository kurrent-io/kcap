using Capacitor.Cli.Commands;
using Capacitor.Cli.Services;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>A <c>service start</c> typed while another operation holds the service's lock, as the desktop app
/// does while it restarts a daemon it saw stop: it waits that operation out, and when it cannot, names it.</summary>
[NotInParallel]
public class DaemonCommandsServiceStartTests {
    const string Name = "svc";

    [TempHome]        public required TempHome        Home    { get; init; }
    [TempDaemonPaths] public required TempDaemonStore Daemons { get; init; }
    [TempConfigRoot]  public required TempConfigRoot  Config  { get; init; }

    DaemonServiceCommands Commands(TimeProvider time) =>
        new(Daemons.Store, Config.Root, Resolutions.None(Config.Root),
            new RecordingServiceManager(new() { [Name] = "/opt/kcap/kcap-daemon" }), Name, Home, time);

    static async Task AdvanceUntil(FakeTimeProvider time, Func<bool> done, string what) {
        for (var steps = 0; !done(); steps++) {
            if (steps > 3000) throw new TimeoutException($"{what} never happened under the fake clock");
            time.Advance(TimeSpan.FromMilliseconds(100));
            await Task.Delay(5);
        }
    }

    [Test]
    public async Task A_start_waits_out_another_operation_and_then_starts() {
        var time = new FakeTimeProvider();
        var held = await ServiceTxnLock.TryAcquireAsync(Daemons.Store, Name, TimeSpan.Zero, TimeProvider.System);
        await Assert.That(held).IsNotNull();
        using var console = ConsoleOutput.StartFullCapture();

        var run = Commands(time).Start([]);
        await AdvanceUntil(time, () => console.GetCapturedError().Contains("Waiting up to", StringComparison.Ordinal), "the waiting line");

        held!.Dispose();
        await AdvanceUntil(time, () => run.IsCompleted, "the start");

        await Assert.That(await run).IsEqualTo(0);
        await Assert.That(console.GetCapturedOutput()).Contains($"Service '{Name}' started.");
        await Assert.That(console.GetCapturedError()).Contains($"Another service operation on '{Name}' is in progress: `");
    }

    [Test]
    public async Task A_start_refused_for_contention_names_what_holds_the_operation() {
        var time = new FakeTimeProvider();
        using var held = await ServiceTxnLock.TryAcquireAsync(Daemons.Store, Name, TimeSpan.Zero, TimeProvider.System);
        await Assert.That(held).IsNotNull();
        using var console = ConsoleOutput.StartFullCapture();

        var run = Commands(time).Start([]);
        await AdvanceUntil(time, () => run.IsCompleted, "the refusal");

        await Assert.That(await run).IsEqualTo(1);
        var error = console.GetCapturedError();
        await Assert.That(error).Contains($"Another service operation is in progress for '{Name}': `");
        await Assert.That(error).Contains($"(pid {Environment.ProcessId}, for ");
        await Assert.That(error).Contains("Try again once it finishes.");
    }

    [Test]
    public async Task A_holder_that_did_not_record_itself_is_not_named() {
        var now = DateTimeOffset.UnixEpoch;

        await Assert.That(DaemonServiceCommands.ContentionLine(Name, null, now))
            .IsEqualTo($"Another service operation is in progress for '{Name}'. Try again shortly.");
        await Assert.That(DaemonServiceCommands.WaitingLine(Name, null, TimeSpan.FromSeconds(58), now))
            .IsEqualTo($"Another service operation on '{Name}' is in progress. Waiting up to 58s for it to finish.");
    }

    [Test]
    public async Task A_recorded_holder_is_named_with_its_command_and_pid() {
        var since  = DateTimeOffset.UnixEpoch;
        var holder = new ServiceTxnHolder(4242, null, since, "kcap daemon service start --verify");

        await Assert.That(DaemonServiceCommands.ContentionLine(Name, holder, since.AddSeconds(70)))
            .IsEqualTo($"Another service operation is in progress for '{Name}': `kcap daemon service start --verify` (pid 4242, for 1m 10s). Try again once it finishes.");
        await Assert.That(DaemonServiceCommands.WaitingLine(Name, holder, TimeSpan.FromSeconds(58), since.AddSeconds(3)))
            .IsEqualTo($"Another service operation on '{Name}' is in progress: `kcap daemon service start --verify` (pid 4242, for 3s). Waiting up to 58s for it to finish.");
    }
}
