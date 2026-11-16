using Capacitor.Cli.Core.Resume;

namespace Capacitor.Cli.Core.Tests.Unit.Resume;

public class ResumeEntryLockTests {
    [TempDir] public required TempDir Tmp { get; init; }

    [Test]
    public async Task A_second_holder_waits_out_its_budget_while_the_first_holds() {
        var entry = Tmp.PathTo("entry.json");
        using var first = ResumeEntryLock.TryAcquire(entry, TimeSpan.FromSeconds(1));

        var second = ResumeEntryLock.TryAcquire(entry, TimeSpan.FromMilliseconds(60));

        await Assert.That(first).IsNotNull();
        await Assert.That(second).IsNull();
    }

    [Test]
    public async Task The_lock_is_free_again_once_released() {
        var entry = Tmp.PathTo("entry.json");
        ResumeEntryLock.TryAcquire(entry, TimeSpan.FromSeconds(1))!.Dispose();

        using var again = ResumeEntryLock.TryAcquire(entry, TimeSpan.FromMilliseconds(60));

        await Assert.That(again).IsNotNull();
    }

    /// <summary>The budget is long enough that a retry loop over the missing directory fails the elapsed check.</summary>
    [Test]
    public async Task A_missing_directory_returns_null_without_waiting_out_the_budget() {
        var entry = Path.Combine(Tmp.PathTo("absent"), "entry.json");
        var start = TimeProvider.System.GetTimestamp();

        var held = ResumeEntryLock.TryAcquire(entry, TimeSpan.FromSeconds(30));

        await Assert.That(held).IsNull();
        await Assert.That(TimeProvider.System.GetElapsedTime(start)).IsLessThan(TimeSpan.FromSeconds(10));
        await Assert.That(Directory.Exists(Tmp.PathTo("absent"))).IsFalse();
    }
}
