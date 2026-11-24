using System.Reactive.Linq;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;

namespace Capacitor.App.Tests.Unit.Services;

public class AppStateStoreTests {
    [Test]
    public async Task Missing_file_yields_defaults() {
        using var tmp = TempDir.WithPathTo("app-state.json", out var path);
        var store = new AppStateStore(path);
        var state = await store.LoadAsync();
        await Assert.That(state).IsEqualTo(new AppState());
    }

    [Test]
    public async Task Corrupt_file_yields_defaults_without_throwing() {
        using var tmp = TempDir.WithPathTo("app-state.json", out var path);
        File.WriteAllText(path, "{not json");
        var store = new AppStateStore(path);
        var state = await store.LoadAsync();
        await Assert.That(state).IsEqualTo(new AppState());
    }

    /// A state file written before the close-to-tray hint existed reads as not yet explained.
    [Test]
    public async Task A_state_file_without_the_close_to_tray_flag_reads_it_as_unexplained() {
        using var tmp = TempDir.WithPathTo("app-state.json", out var path);
        File.WriteAllText(path, """{"shim_offered":true}""");

        var state = await new AppStateStore(path).LoadAsync();

        await Assert.That(state.ShimOffered).IsTrue();
        await Assert.That(state.CloseToTrayExplained).IsFalse();
    }

    [Test]
    public async Task Update_then_fresh_store_load_sees_it() {
        using var tmp = TempDir.WithPathTo("app-state.json", out var path);
        var store = new AppStateStore(path);
        var ok = await store.UpdateAsync(s => s with { ShimOffered = true, ShimDenied = true });
        await Assert.That(ok).IsTrue();

        var reloaded = new AppStateStore(path);
        var state = await reloaded.LoadAsync();
        await Assert.That(state.ShimOffered).IsTrue();
        await Assert.That(state.ShimDenied).IsTrue();
    }

    [Test]
    public async Task Update_creates_missing_parent_directory() {
        using var tmp = new TempDir();
        var path = tmp.PathTo("nested", "sub", "app-state.json");
        var store = new AppStateStore(path);

        var ok = await store.UpdateAsync(s => s with { ShimOffered = true });

        await Assert.That(ok).IsTrue();
        await Assert.That(File.Exists(path)).IsTrue();
    }

    [Test]
    public async Task No_tmp_file_left_behind_after_successful_write() {
        using var tmp = TempDir.WithPathTo("app-state.json", out var path);
        var store = new AppStateStore(path);
        await store.UpdateAsync(s => s with { ShimOffered = true });

        var leftovers = Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp");
        await Assert.That(leftovers).IsEmpty();
    }

    [Test]
    public async Task Fifty_parallel_updates_all_land() {
        using var tmp = TempDir.WithPathTo("app-state.json", out var path);
        var store = new AppStateStore(path);

        var tasks = Enumerable.Range(0, 50).Select(i =>
            store.UpdateAsync(s => s with {
                HarnessByRepo = new Dictionary<string, string>(s.HarnessByRepo ?? new Dictionary<string, string>()) { [$"/repo/{i}"] = "claude" }
            }));
        var results = await Task.WhenAll(tasks);

        await Assert.That(results.All(r => r)).IsTrue();

        var final = await store.LoadAsync();
        await Assert.That(final.HarnessByRepo!.Count).IsEqualTo(50);
    }

    [Test]
    public async Task Write_failure_when_parent_is_a_regular_file_returns_false_without_throwing() {
        using var tmp = new TempDir();
        var blockingFile = tmp.PathTo("blocked");
        File.WriteAllText(blockingFile, "not a directory");
        var path = Path.Combine(blockingFile, "app-state.json"); // parent path component is a file

        var store = new AppStateStore(path);
        var ok = await store.UpdateAsync(s => s with { ShimOffered = true });

        await Assert.That(ok).IsFalse();
    }

    [Test]
    public async Task Harness_choice_is_remembered_per_repo() {
        using var tmp = TempDir.WithPathTo("app-state.json", out var path);
        var store = new AppStateStore(path);

        await store.UpdateAsync(s => s with {
            HarnessByRepo = new Dictionary<string, string> {
                ["/home/a/kcap-cli"] = "codex",
                ["/home/a/kcap-web"] = "kiro",
            }
        });

        var reloaded = await new AppStateStore(path).LoadAsync();

        await Assert.That(reloaded.HarnessByRepo!["/home/a/kcap-cli"]).IsEqualTo("codex");
        await Assert.That(reloaded.HarnessByRepo!["/home/a/kcap-web"]).IsEqualTo("kiro");
    }

    [Test]
    public async Task Account_memory_round_trips_through_the_file() {
        using var tmp = TempDir.WithPathTo("app-state.json", out var path);
        var store = new AppStateStore(path);

        await store.UpdateAsync(s => s with {
            AccountByRepo = AccountMemory.With(s.AccountByRepo, "host1", "claude", "/home/a/kcap-cli", remote: false, "a1"),
        });

        var reloaded = await new AppStateStore(path).LoadAsync();

        await Assert.That(AccountMemory.Lookup(reloaded.AccountByRepo, "host1", "claude", "/home/a/kcap-cli", remote: false)).IsEqualTo("a1");
        await Assert.That(AccountMemory.Lookup(reloaded.AccountByRepo, "host1", "codex", "/home/a/kcap-cli", remote: false)).IsNull();
    }

    [Test]
    public async Task Window_placement_is_remembered() {
        using var tmp = TempDir.WithPathTo("app-state.json", out var path);
        var store = new AppStateStore(path);

        await store.UpdateAsync(s => s with {
            WindowWidth = 1552, WindowHeight = 888, WindowX = 120, WindowY = 80, WindowMaximized = true,
        });

        var reloaded = await new AppStateStore(path).LoadAsync();
        await Assert.That(reloaded.WindowWidth).IsEqualTo(1552);
        await Assert.That(reloaded.WindowHeight).IsEqualTo(888);
        await Assert.That(reloaded.WindowX).IsEqualTo(120);
        await Assert.That(reloaded.WindowY).IsEqualTo(80);
        await Assert.That(reloaded.WindowMaximized).IsTrue();
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task Reading_positions_retry_after_write_failure_and_keep_a_bounded_metadata_history() => AvaloniaSession.RunOnUiAsync(async () => {
        using var tmp = new TempDir();
        var path = tmp.CreateDir("app-state.json");
        var state = new AppStateStore(path);
        var subjects = Observable.Return<string?>("reader");
        var positions = new ChatReadingPositions(state, "first profile", subjects);
        var key = positions.Key("reader", AgentOrigin.Local, "session", "private/transcript.jsonl");
        var position = new ChatReadingPosition(new ChatMessageKey(114, 0), 13, new string('A', 32));
        await positions.ReadAsync(key);
        positions.Remember(key, position);
        await positions.FlushAsync();
        await Assert.That(await positions.ReadAsync(key)).IsEqualTo(position);

        Directory.Delete(path);
        await state.UpdateAsync(old => old with { UiScale = 1.3 });
        await positions.FlushAsync();
        var fresh = new ChatReadingPositions(new AppStateStore(path), "first profile", subjects);
        await Assert.That(await fresh.ReadAsync(key)).IsEqualTo(position);
        var other = new ChatReadingPositions(state, "second profile", subjects);
        await Assert.That(await other.ReadAsync(other.Key("reader", AgentOrigin.Local, "session", "private/transcript.jsonl"))).IsNull();
        var json = File.ReadAllText(path);
        await Assert.That(json.Contains("private/transcript.jsonl", StringComparison.Ordinal)).IsFalse();
        await Assert.That(json.Contains("reader", StringComparison.Ordinal)).IsFalse();

        for (var i = 0; i < ChatReadingPositions.Capacity + 1; i++)
            fresh.Remember(fresh.Key("reader", AgentOrigin.Remote, $"session-{i}", $"session-{i}"), ChatReadingPosition.Live);
        await fresh.FlushAsync();
        var saved = await state.LoadAsync();
        await Assert.That(saved.UiScale).IsEqualTo(1.3);
        await Assert.That(saved.ChatReadingPositions!.Count).IsEqualTo(ChatReadingPositions.Capacity);
        await Assert.That(saved.ChatReadingPositions!.ContainsKey(key)).IsFalse();
    });
}
