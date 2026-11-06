using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Harness.Antigravity;
using Capacitor.Cli.Core.Harness.Cursor;
using Capacitor.Cli.Recording.Harness.Muse;
using Capacitor.Cli.Recording.Harness.Titles;
using Capacitor.Cli.Tests.Unit.Harness.Antigravity;
using Capacitor.Cli.Tests.Unit.Harness.Cursor;
using TUnit.Core.Enums;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class WatchRecorderHostTests {
    [TempHome] public required TempHome Home { get; init; }

    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    const string Id = "0199a3b2-1c2d-7e8f-9a0b-1c2d3e4f5a6b";

    HarnessRegistry Harnesses => TestHarnesses.Under(Home);

    WatchRecorderHost Host => HostOn(new WatcherPaths(Config.PathTo("watchers")), Config.Root);

    WatchRecorderHost HostOn(WatcherPaths paths, ConfigRoot config) {
        var http     = new FixedCapacitorHttpClient();
        var resolved = Resolutions.None(config);

        return new WatchRecorderHost(config, resolved, Harnesses, http, TestWatchers.In(paths, config, resolved, http), TimeProvider.System, () => { }, () => { });
    }

    static string Write(string path, string content) {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);

        return path;
    }

    static readonly DateTimeOffset T0 = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);

    static Dictionary<string, int> Ready(params string[] keys) => keys.ToDictionary(k => k, _ => 1);

    /// <summary>A kill that fails leaves the child's watcher running, so the stop is confirmed only by
    /// the retry that ends it.</summary>
    [Test]
    public async Task A_child_is_confirmed_stopped_only_once_its_watcher_is_gone() {
        using var config = new TempConfigRoot();
        var paths = new WatcherPaths(config.PathTo("watchers"));
        Directory.CreateDirectory(paths.Directory);
        var host = HostOn(paths, config.Root);

        using var watcher = DummyProcess.StartSleep(30);
        var       pidFile = paths.PidFile("s-a");
        await File.WriteAllTextAsync(pidFile, $"{watcher.Pid}\n{PidIdentity.Capture(watcher.Pid)}");

        var children = new MuseChildWatchers();
        children.Spawned("s-a", "/a/session.jsonl", T0);
        children.SelectStops(Ready("s-a"), T0);
        var now   = T0 + MuseChildWatchers.StopGrace;
        var ready = Ready("s-a");

        int failed;
        using (new FileStream(pidFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            failed = await children.Stop(host, children.SelectStops(ready, now), ready);

        await Assert.That(failed).IsEqualTo(0);
        await Assert.That(watcher.HasExited).IsFalse();
        await Assert.That(children.RecheckPending).IsTrue();

        var retried = await children.Stop(host, children.SelectStops(ready, now.AddSeconds(1)), ready);

        await Assert.That(retried).IsEqualTo(1);
        await Assert.That(watcher.WaitForExit(TimeSpan.FromSeconds(5))).IsTrue();
        await Assert.That(children.RecheckPending).IsFalse();
        await Assert.That(children.SelectStops(ready, now.AddSeconds(2))).IsEmpty();
    }

    string CursorTranscript() =>
        Home.CreateFile([".cursor", "projects", "Users-me-work", "agent-transcripts", Id, $"{Id}.jsonl"]);

    // Windows keeps the IDE's store under the real Roaming AppData, which no test home can stand in for.
    [Test, ExcludeOn(OS.Windows)]
    public async Task Cursor_reads_the_chat_meta_json_first() {
        Home.CreateFile([".cursor", "chats", "0164d96655f5a60b647a4bd178a34425", Id, "meta.json"], """{"title":"From meta"}""");
        CursorComposerTitleTests.BuildStateDb(Write(Harnesses.Of<CursorHarness>().Paths.GlobalStateDb!, ""), Id, "From composer");

        var store = HarnessTitleStores.For("cursor", null, Id, CursorTranscript(), Harnesses, Host);

        await Assert.That(store!.Read()!.Title).IsEqualTo("From meta");
    }

    [Test, ExcludeOn(OS.Windows)]
    public async Task Cursor_falls_back_to_the_ide_composer_record() {
        CursorComposerTitleTests.BuildStateDb(Write(Harnesses.Of<CursorHarness>().Paths.GlobalStateDb!, ""), Id, "From composer");

        var store = HarnessTitleStores.For("cursor", null, Id, CursorTranscript(), Harnesses, Host);

        await Assert.That(store!.Read()!.Title).IsEqualTo("From composer");
    }

    [Test]
    public async Task Antigravity_reads_the_roots_summary_db() {
        const string conversationId = AntigravitySummaryTitleTests.ConversationId;
        var paths = Harnesses.Of<AntigravityHarness>().Paths;
        AntigravitySummaryTitleTests.BuildSummaryDb(Write(Path.Combine(paths.CliConfigRoot, "conversation_summaries.db"), ""), "Antigravity name");
        var transcript = Write(paths.TranscriptFullPathUnder(paths.CliConfigRoot, conversationId), "");

        var store = HarnessTitleStores.For("antigravity", null, conversationId.Replace("-", ""), transcript, Harnesses, Host);

        await Assert.That(store!.Read()!.Title).IsEqualTo("Antigravity name");
    }
}
