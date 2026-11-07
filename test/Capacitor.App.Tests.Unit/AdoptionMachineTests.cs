using Capacitor.App.Services.CliAdoption;
using Microsoft.Extensions.Time.Testing;
using TUnit.Core.Enums;

namespace Capacitor.App.Tests.Unit;

[ExcludeOn(OS.Windows)]
public class AdoptionMachineTests {
    static readonly TimeSpan Bound = TimeSpan.FromMinutes(3);

    /// <summary>No process at the pid: a helper whose start identity was never read is gone, not ambiguous. The bound
    /// is never reached, since time does not move.</summary>
    [Test]
    public async Task A_pid_no_process_has_ends_the_wait_without_a_start_token() {
        var wait = AdoptionMachine.WaitForExitAsync(int.MaxValue, startToken: null, Bound, new FakeTimeProvider(), CancellationToken.None);

        await Assert.That(await wait.WaitAsync(TimeSpan.FromSeconds(10))).IsTrue();
    }

    /// <summary>A pid still present, here launchd's or init's, may be a later process when no start identity was read:
    /// only the bound ends the wait, and the process is then taken as possibly still the one waited for.</summary>
    [Test]
    public async Task A_present_pid_without_a_start_token_is_waited_for_the_whole_bound() {
        var time = new FakeTimeProvider();
        var wait = AdoptionMachine.WaitForExitAsync(1, startToken: null, Bound, time, CancellationToken.None);

        await Task.Delay(TimeSpan.FromMilliseconds(200));
        await Assert.That(wait.IsCompleted).IsFalse();

        time.Advance(Bound + TimeSpan.FromSeconds(1));
        await Assert.That(await wait.WaitAsync(TimeSpan.FromSeconds(10))).IsFalse();
    }
}
