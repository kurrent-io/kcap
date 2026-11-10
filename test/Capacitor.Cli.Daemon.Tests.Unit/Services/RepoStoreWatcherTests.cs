using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>
/// The watcher that notices <c>repos.json</c>, or a repository it names, changing under a running
/// daemon and re-sends the repo paths, so the launch dialog follows without a restart.
/// </summary>
public class RepoStoreWatcherTests {
    [TempDir] public required TempDir Tmp { get; init; }

    static readonly RepoStoreFingerprint Before = new("a");
    static readonly RepoStoreFingerprint After  = new("b");

    sealed class Harness {
        public RepoStoreFingerprint? File;
        public RepoAdvertisement?    Advertised;
        public bool                  Ready = true;
        public int                   Publishes;
        public Exception?            PublishThrow;
        public readonly RepoStoreWatcher Watcher;

        public Harness() {
            Watcher = RepoStoreWatcher.ForTest(
                stat: () => File,
                advertised: () => Advertised,
                isReady: () => Ready,
                time: TimeProvider.System,
                publish: () => {
                    Publishes++;
                    if (PublishThrow is { } ex) return Task.FromException(ex);
                    Advertised = Sent(File);
                    return Task.CompletedTask;
                });
        }
    }

    static RepoAdvertisement Sent(RepoStoreFingerprint? store, string[]? paths = null, string[]? missing = null) =>
        new(store, paths ?? [], missing ?? []);

    [Test]
    public async Task An_unchanged_file_sends_nothing() {
        var h = new Harness { File = Before, Advertised = Sent(Before) };

        await h.Watcher.TickAsync();

        await Assert.That(h.Publishes).IsEqualTo(0);
    }

    [Test]
    public async Task A_changed_file_is_sent_once() {
        var h = new Harness { File = After, Advertised = Sent(Before) };

        await h.Watcher.TickAsync();
        await h.Watcher.TickAsync();

        await Assert.That(h.Publishes).IsEqualTo(1);
    }

    [Test]
    public async Task A_file_that_appears_is_sent() {
        var h = new Harness { File = Before, Advertised = null };

        await h.Watcher.TickAsync();

        await Assert.That(h.Publishes).IsEqualTo(1);
    }

    [Test]
    public async Task A_file_that_disappears_is_sent() {
        var h = new Harness { File = null, Advertised = Sent(Before) };

        await h.Watcher.TickAsync();

        await Assert.That(h.Publishes).IsEqualTo(1);
    }

    /// Deleting a clone leaves repos.json as it was, so only the directory itself says the list is stale.
    [Test]
    public async Task An_advertised_repository_that_is_deleted_is_sent() {
        var repo = Tmp.CreateDir("repo").Path;
        var h = new Harness { File = Before, Advertised = Sent(Before, paths: [repo]) };

        await h.Watcher.TickAsync();
        await Assert.That(h.Publishes).IsEqualTo(0);

        Directory.Delete(repo);
        await h.Watcher.TickAsync();

        await Assert.That(h.Publishes).IsEqualTo(1);
    }

    /// A volume mounted again, or a clone made again at the same path, brings its entry back.
    [Test]
    public async Task A_left_out_repository_that_reappears_is_sent() {
        var repo = Tmp.PathTo("repo");
        var h = new Harness { File = Before, Advertised = Sent(Before, missing: [repo]) };

        await h.Watcher.TickAsync();
        await Assert.That(h.Publishes).IsEqualTo(0);

        Directory.CreateDirectory(repo);
        await h.Watcher.TickAsync();

        await Assert.That(h.Publishes).IsEqualTo(1);
    }

    // Registration sends the file as it is then; a send before it would only race it.
    [Test]
    public async Task Nothing_is_sent_until_the_daemon_is_registered() {
        var h = new Harness { File = After, Advertised = Sent(Before), Ready = false };

        await h.Watcher.TickAsync();
        await Assert.That(h.Publishes).IsEqualTo(0);

        h.Ready = true;
        await h.Watcher.TickAsync();
        await Assert.That(h.Publishes).IsEqualTo(1);
    }

    [Test]
    public async Task A_failed_send_is_retried_on_the_next_tick_and_never_escapes() {
        var h = new Harness { File = After, Advertised = Sent(Before), PublishThrow = new IOException("hub down") };

        await h.Watcher.TickAsync();
        h.PublishThrow = null;
        await h.Watcher.TickAsync();
        await h.Watcher.TickAsync();

        await Assert.That(h.Publishes).IsEqualTo(2);
    }
}
