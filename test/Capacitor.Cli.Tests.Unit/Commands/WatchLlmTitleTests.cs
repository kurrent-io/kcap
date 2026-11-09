using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class WatchLlmTitleTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    /// <summary>The generator that finishes a stopped watcher's title reads the prompt and reply as the watch loop does:
    /// a slash command is no prompt to title from, and the reply is cut to what the title prompt takes.</summary>
    [Test]
    public async Task A_stopped_watchers_title_is_read_from_its_transcript_as_the_watch_loop_reads_it() {
        using var tmp = new TempDir();
        var reply = new string('r', 400);
        var path  = tmp.CreateFile("t.jsonl", string.Join("\n",
            """{"type":"user","message":{"content":"<command-name>/clear</command-name>"}}""",
            """{"type":"user","message":{"content":"Summarise the README"}}""",
            $$$"""{"type":"assistant","message":{"content":[{"type":"text","text":"{{{reply}}}"}]}}""") + "\n");

        var (user, assistant) = WatchCommand.ReadTitleContext(path, "claude", TimeProvider.System);

        await Assert.That(user).IsEqualTo("Summarise the README");
        await Assert.That(assistant).IsEqualTo(reply[..300]);
    }

    /// <summary>Anyone on the machine can read a command line, so the generator is told where the transcript is, never
    /// what it says.</summary>
    [Test]
    public async Task The_title_generator_is_handed_the_transcript_not_the_prompt() {
        using var tmp     = new TempDir();
        var       root    = Config.Root;
        var       good    = Resolutions.At("http://127.0.0.1:1", root);
        var       starter = FakeProcessStarter.Refusing();
        var       paths   = WatcherPaths.FromEnvironment(root);
        var       manager = new WatcherManager(root, good, new FixedCapacitorHttpClient(), starter, paths,
            new ProcessWatcherSpawner(root, good, paths, starter, TimeProvider.System), TimeProvider.System);

        manager.SpawnTitleGenerator("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "pi", tmp.PathTo("t.jsonl"), TextWriter.Null);

        await Assert.That(string.Join(" ", starter.Seen!.ArgumentList))
            .IsEqualTo($"generate-title aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa pi {tmp.PathTo("t.jsonl")}");
    }
}
