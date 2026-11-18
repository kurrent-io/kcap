namespace Capacitor.Cli.Core.Tests.Unit;

public class WatcherLivenessTests {
    const string Key = "session";

    [TempDir] public required TempDir Tmp { get; init; }

    WatcherPaths Paths => field ??= new(Tmp.CreateDir("watchers"));

    WatcherLiveness Liveness => field ??= new(Paths, TimeProvider.System);

    [Test]
    public async Task The_loser_of_the_spawn_lock_skips_rather_than_waits() {
        var ran = false;

        await Liveness.WithSpawnLock(Key, () => Liveness.WithSpawnLock(Key, () => {
            ran = true;

            return Task.CompletedTask;
        }));

        await Assert.That(ran).IsFalse();
    }

    [Test]
    public async Task The_spawn_lock_is_free_again_once_its_body_throws() {
        await Assert.That(() => Liveness.WithSpawnLock(Key, () => throw new InvalidOperationException("boom"))).Throws<InvalidOperationException>();

        var ran = false;

        await Liveness.WithSpawnLock(Key, () => {
            ran = true;

            return Task.CompletedTask;
        });

        await Assert.That(ran).IsTrue();
    }

    [Test]
    public async Task A_spawn_lock_nothing_holds_can_be_deleted_and_a_held_one_cannot() {
        await Liveness.WithSpawnLock(Key, async () => {
            await Assert.That(WatcherLiveness.TryDeleteSpawnLock(Paths.SpawnLockFile(Key))).IsFalse();
            await Assert.That(File.Exists(Paths.SpawnLockFile(Key))).IsTrue();
        });

        await Assert.That(WatcherLiveness.TryDeleteSpawnLock(Paths.SpawnLockFile(Key))).IsTrue();
        await Assert.That(File.Exists(Paths.SpawnLockFile(Key))).IsFalse();
    }

    [Test]
    public async Task No_pid_file_is_no_watcher() =>
        await Assert.That(Liveness.PidAlive(Key)).IsFalse();

    [Test]
    public async Task A_pid_file_that_cannot_be_read_counts_as_alive() {
        Tmp.CreateDir("watchers", $"{Key}.pid");

        await Assert.That(Liveness.PidAlive(Key)).IsTrue();
    }

    [Test]
    public async Task A_live_pid_with_no_heartbeat_past_its_grace_is_not_alive() {
        Tmp.CreateFile($"watchers/{Key}.pid", $"{Environment.ProcessId}");
        WatcherHeartbeat.Touch(Paths.StartedFile(Key), DateTimeOffset.UtcNow - WatcherHeartbeat.Grace - WatcherHeartbeat.Threshold - TimeSpan.FromMinutes(1));

        await Assert.That(Liveness.PidAlive(Key)).IsTrue();
        await Assert.That(Liveness.IsWatcherAlive(Key)).IsFalse();
    }
}
