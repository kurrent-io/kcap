using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>
/// A repo-path send records which version of <c>repos.json</c> the server now holds and which of its
/// paths were left out, and the production watcher compares the disk against exactly that record.
/// </summary>
public class ServerConnectionRepoPathsTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }
    [TempDir] public required TempDir Tmp { get; init; }

    sealed class RepoPathsServerConnection(DaemonConfig config) : ServerConnection(
        config, new FixedCredentialSource(), NullLoggerFactory.Instance, NullLogger<ServerConnection>.Instance, TimeProvider.System) {
        public readonly List<string[]>       Sent        = [];
        public readonly TaskCompletionSource SendEntered = new();
        public          TaskCompletionSource? SendGate;
        public          Exception?            SendThrow;
        public          bool                  Ready = true;

        internal override bool IsReady => Ready;

        internal override async Task SendRepoPathsAsync(string[] repoPaths) {
            Sent.Add(repoPaths);
            SendEntered.TrySetResult();
            if (Interlocked.Exchange(ref SendGate, null) is { } gate) await gate.Task;
            if (SendThrow is { } ex) throw ex;
        }
    }

    DaemonConfig NewConfig() => new() { Name = "test", ServerUrl = "http://127.0.0.1:1", ConfigRoot = Config.Root };

    RepoPathStore Store() => new(Config.Root, TimeProvider.System);

    [Test]
    public async Task A_send_carries_the_persisted_paths_and_records_the_file_it_read() {
        var a = Tmp.CreateDir("project-a").Path;
        var store = Store();
        await store.AddAsync(a);
        await using var conn = new RepoPathsServerConnection(NewConfig());

        await conn.UpdateRepoPathsAsync();

        await Assert.That(conn.Sent.Count).IsEqualTo(1);
        await Assert.That(conn.Sent[0]).IsEquivalentTo(new[] { Path.GetFullPath(a) });
        await Assert.That(conn.AdvertisedRepos!.Store).IsEqualTo(store.Fingerprint());
    }

    /// repos.json keeps a deleted clone; the server is never offered it.
    [Test]
    public async Task A_persisted_path_that_is_not_a_directory_is_left_out_and_recorded() {
        var kept = Tmp.CreateDir("kept").Path;
        var gone = Tmp.PathTo("gone");
        var store = Store();
        await store.AddAsync(kept);
        await store.AddAsync(gone);
        await using var conn = new RepoPathsServerConnection(NewConfig());

        await conn.UpdateRepoPathsAsync();

        await Assert.That(conn.Sent[0]).IsEquivalentTo(new[] { Path.GetFullPath(kept) });
        await Assert.That(conn.AdvertisedRepos!.Missing).IsEquivalentTo(new[] { Path.GetFullPath(gone) });
        await Assert.That((await store.LoadAsync()).Length).IsEqualTo(2).Because("hiding a path never deletes its entry");
    }

    [Test]
    public async Task A_failed_send_records_nothing() {
        await Store().AddAsync(Tmp.CreateDir("project-a").Path);
        await using var conn = new RepoPathsServerConnection(NewConfig()) { SendThrow = new IOException("hub down") };

        await conn.UpdateRepoPathsAsync();

        await Assert.That(conn.AdvertisedRepos).IsNull();
    }

    /// The daemon's own launch path and the watcher can send at the same time, and the server runs
    /// one client's invocations in parallel: an older list processed after a newer one, with the
    /// newer fingerprint recorded last, would leave the server stale with nothing left to repair it.
    [Test]
    public async Task Overlapping_sends_run_one_at_a_time() {
        var a = Tmp.CreateDir("project-a").Path;
        var b = Tmp.CreateDir("project-b").Path;
        var store = Store();
        await store.AddAsync(a);
        var gate = new TaskCompletionSource();
        await using var conn = new RepoPathsServerConnection(NewConfig()) { SendGate = gate };

        var first = conn.UpdateRepoPathsAsync();
        await conn.SendEntered.Task;
        await store.AddAsync(b);
        var second = conn.UpdateRepoPathsAsync();
        // Waiting for a negative: the second send must not start while the first is held open.
        await Task.Delay(100);
        await Assert.That(conn.Sent.Count).IsEqualTo(1);

        gate.SetResult();
        await first;
        await second;

        await Assert.That(conn.Sent.Count).IsEqualTo(2);
        await Assert.That(conn.Sent[1]).IsEquivalentTo(new[] { Path.GetFullPath(a), Path.GetFullPath(b) });
        await Assert.That(conn.AdvertisedRepos!.Store).IsEqualTo(store.Fingerprint());
    }

    /// The watcher as DI builds it reads the file the connection advertises: a write from
    /// another process is sent exactly once, and an untouched file is never re-sent.
    [Test]
    public async Task The_watcher_sends_a_write_from_another_process_exactly_once() {
        var config = NewConfig();
        await using var conn    = new RepoPathsServerConnection(config);
        using var       watcher = new RepoStoreWatcher(config, conn, NullLogger<RepoStoreWatcher>.Instance, TimeProvider.System);

        await watcher.TickAsync();
        await Assert.That(conn.Sent).IsEmpty();

        await Store().AddAsync(Tmp.CreateDir("project-a").Path);
        await watcher.TickAsync();
        await watcher.TickAsync();

        await Assert.That(conn.Sent.Count).IsEqualTo(1);
    }

    /// Deleting a clone touches no file kcap owns; the watcher as DI builds it still drops it from the
    /// server's list, once.
    [Test]
    public async Task The_watcher_drops_a_deleted_repository_exactly_once() {
        var a = Tmp.CreateDir("project-a").Path;
        var b = Tmp.CreateDir("project-b").Path;
        var store = Store();
        await store.AddAsync(a);
        await store.AddAsync(b);
        var config = NewConfig();
        await using var conn    = new RepoPathsServerConnection(config);
        using var       watcher = new RepoStoreWatcher(config, conn, NullLogger<RepoStoreWatcher>.Instance, TimeProvider.System);
        await conn.UpdateRepoPathsAsync();

        Directory.Delete(b);
        await watcher.TickAsync();
        await watcher.TickAsync();

        await Assert.That(conn.Sent.Count).IsEqualTo(2);
        await Assert.That(conn.Sent[1]).IsEquivalentTo(new[] { Path.GetFullPath(a) });
    }
}
