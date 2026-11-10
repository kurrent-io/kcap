using System.Collections.Concurrent;
using System.Diagnostics;

namespace Capacitor.Cli.Core.Tests.Unit;

/// <summary>Drains of one spool never run at once: they read and rewrite the same temp files, so a second
/// drain would post what the first still holds. A drain that finds the spool busy skips rather than waits.</summary>
public class SpoolDrainLeaseTests {
    [TempDir] public required TempDir Tmp { get; init; }

    const string Sid = "0123456789abcdef0123456789abcdef";

    static Task<DrainOutcome> Delivered(ConcurrentBag<string> posts, string route) {
        posts.Add(route);
        return Task.FromResult(DrainOutcome.Delivered);
    }

    /// <summary>The first drain holds its first post until the second has run.</summary>
    static async Task<List<string>> TwoDrainsAsync(Func<Func<string, string, Task<DrainOutcome>>, Task> drain) {
        var posts   = new ConcurrentBag<string>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hold    = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = drain(async (route, _) => {
            posts.Add(route);
            entered.TrySetResult();
            await hold.Task;
            return DrainOutcome.Delivered;
        });
        await entered.Task;
        await drain((route, _) => Delivered(posts, route));
        hold.SetResult();
        await first;

        return posts.ToList();
    }

    HookSpool Spool() {
        var spool = new HookSpool(Tmp.PathTo("spool"), TimeProvider.System);
        spool.Append(Sid, "session-start/codex", """{"n":1}""");
        spool.Append(Sid, "session-end/codex", """{"n":2}""");
        return spool;
    }

    [Test]
    public async Task Two_overlapping_hook_drains_never_post_the_same_entry() {
        var spool = Spool();

        var posts = await TwoDrainsAsync(poster => spool.DrainAllAsync(null, poster, TimeSpan.FromSeconds(30), CancellationToken.None));

        await Assert.That(posts.Count).IsEqualTo(posts.Distinct().Count());
    }

    [Test]
    public async Task Two_overlapping_ordered_drains_never_post_the_same_entry() {
        using var nothingDisabled = new TempConfigRoot();
        var spool      = Spool();
        var transcript = new TranscriptSpool(Tmp.PathTo("transcripts"), TimeProvider.System);

        var posts = await TwoDrainsAsync(poster => LifecycleSpoolDrain.RunAsync(
            nothingDisabled.Root, spool, transcript, null, poster, _ => Task.FromResult(DrainOutcome.Delivered), TimeSpan.FromSeconds(30), TimeProvider.System, CancellationToken.None));

        await Assert.That(posts.Count).IsEqualTo(posts.Distinct().Count());
    }

    [Test]
    public async Task A_lease_held_by_a_process_that_died_is_taken() {
        Skip.When(OperatingSystem.IsWindows(), "the holder is a perl flock, and the Windows runners have no perl");
        var dir = Tmp.CreateDir("spool");

        using var holder = Process.Start(new ProcessStartInfo("perl", [
            "-e", "use Fcntl qw(:flock); open(my $f, '>>', $ARGV[0]) or die; flock($f, LOCK_EX) or die; $| = 1; print \"held\\n\"; sleep 60;",
            Path.Combine(dir, SpoolDrainLease.FileName),
        ]) { RedirectStandardOutput = true, UseShellExecute = false })!;
        await Assert.That(await holder.StandardOutput.ReadLineAsync()).IsEqualTo("held");

        await Assert.That(SpoolDrainLease.TryTake(dir)).IsNull();

        holder.Kill();
        await holder.WaitForExitAsync();

        using var taken = SpoolDrainLease.TryTake(dir);
        await Assert.That(taken).IsNotNull();
    }
}
