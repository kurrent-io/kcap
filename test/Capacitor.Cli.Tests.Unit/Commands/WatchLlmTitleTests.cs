using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class WatchLlmTitleTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    static WatchState Exchanged(int events, string? reply) => new() {
        ThresholdReached   = true,
        FirstUserText      = "Summarise the README",
        FirstAssistantText = reply,
        EventCount         = events,
    };

    /// <summary>A one-shot run (a prompt and its answer) ends well short of five events.</summary>
    [Test]
    public async Task A_prompt_and_its_reply_are_titled_below_five_events() =>
        await Assert.That(WatchCommand.ShouldGenerateLlmTitle(Exchanged(events: 2, reply: "The README covers setup."), agentId: null)).IsTrue();

    [Test]
    public async Task Without_a_reply_titling_still_waits_for_five_events() {
        await Assert.That(WatchCommand.ShouldGenerateLlmTitle(Exchanged(events: 4, reply: null), agentId: null)).IsFalse();
        await Assert.That(WatchCommand.ShouldGenerateLlmTitle(Exchanged(events: 5, reply: null), agentId: null)).IsTrue();
    }

    [Test]
    public async Task A_reply_does_not_override_the_other_gates() {
        var harnessTitled = Exchanged(events: 2, reply: "Done.");
        harnessTitled.HarnessTitleSeen = true;
        var inFlight = Exchanged(events: 2, reply: "Done.");
        inFlight.TitleInFlight = true;
        var buffering = Exchanged(events: 2, reply: "Done.");
        buffering.ThresholdReached = false;

        await Assert.That(WatchCommand.ShouldGenerateLlmTitle(harnessTitled, agentId: null)).IsFalse();
        await Assert.That(WatchCommand.ShouldGenerateLlmTitle(inFlight, agentId: null)).IsFalse();
        await Assert.That(WatchCommand.ShouldGenerateLlmTitle(buffering, agentId: null)).IsFalse();
        await Assert.That(WatchCommand.ShouldGenerateLlmTitle(Exchanged(events: 2, reply: "Done."), agentId: "sub")).IsFalse();
    }

    /// <summary>A stop request is followed by a kill 5s later, so the title gets what is left of 4s; an exit nothing
    /// kills waits out the CLI's own timeout; a closed connection could not carry the title at all.</summary>
    [Test]
    [Arguments(true, true, 1.5, 2.5)]
    [Arguments(true, true, 5.0, -1.0)]
    [Arguments(true, false, 1.5, 28.5)]
    [Arguments(false, true, 0.0, 0.0)]
    [Arguments(false, false, 0.0, 0.0)]
    public async Task The_shutdown_title_budget_fits_what_follows_the_exit(bool connected, bool stopRequested, double elapsed, double expected) =>
        await Assert.That(WatchCommand.ShutdownTitleBudget(connected, stopRequested, TimeSpan.FromSeconds(elapsed)))
            .IsEqualTo(TimeSpan.FromSeconds(expected));

    /// <summary>The wait returns only once the generation, post included, is done, so the caller closes the connection
    /// after the title went out over it.</summary>
    [Test]
    public async Task A_title_finishing_inside_the_budget_is_sent_before_the_wait_returns() {
        var time    = new FakeTimeProvider();
        var release = new TaskCompletionSource();
        var posted  = false;
        var pending = Task.Run(async () => {
            await release.Task;
            posted = true;
        });

        var waiting = WatchCommand.AwaitPendingTitleAsync(pending, TimeSpan.FromSeconds(4), time, () => { });
        time.Advance(TimeSpan.FromSeconds(2));
        release.SetResult();

        await Assert.That(await waiting.WaitAsync(TimeSpan.FromSeconds(30))).IsTrue();
        await Assert.That(posted).IsTrue();
    }

    /// <summary>One that outlasts the budget is given up on at its end, and the heartbeat is kept fresh throughout.</summary>
    [Test]
    public async Task A_title_outlasting_the_budget_is_given_up_on_with_the_heartbeat_kept() {
        var time    = new FakeTimeProvider();
        var never   = new TaskCompletionSource();
        var beats   = 0;
        var waiting = WatchCommand.AwaitPendingTitleAsync(never.Task, TimeSpan.FromSeconds(12), time, () => Interlocked.Increment(ref beats));

        while (!waiting.IsCompleted) {
            time.Advance(TimeSpan.FromSeconds(1));
            await Task.Yield();
        }

        await Assert.That(await waiting).IsFalse();
        await Assert.That(beats).IsEqualTo(3);
    }

    [Test]
    public async Task A_spent_budget_gives_up_at_once() =>
        await Assert.That(await WatchCommand.AwaitPendingTitleAsync(new TaskCompletionSource().Task, TimeSpan.FromSeconds(-1), new FakeTimeProvider(), () => { }))
            .IsFalse();

    [Test]
    public async Task A_generation_failing_during_the_wait_counts_as_settled() {
        var failing = new TaskCompletionSource();
        var waiting = WatchCommand.AwaitPendingTitleAsync(failing.Task, TimeSpan.FromSeconds(4), new FakeTimeProvider(), () => { });
        failing.SetException(new InvalidOperationException("boom"));

        await Assert.That(await waiting.WaitAsync(TimeSpan.FromSeconds(30))).IsTrue();
    }

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
