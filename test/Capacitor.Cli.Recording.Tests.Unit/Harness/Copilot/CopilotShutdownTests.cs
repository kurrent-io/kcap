using Capacitor.Cli.Core;
using Capacitor.Cli.Recording.Harness.Copilot;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Recording.Tests.Unit.Harness.Copilot;

/// <summary>A watcher ends a Copilot session itself once <c>session.shutdown</c> stays the transcript's last
/// line: the sessionEnd hook does not always fire, and a resume appends to the same file.</summary>
public class CopilotShutdownTests {
    const string Shutdown = """{"type":"session.shutdown","data":{},"id":"e1","timestamp":"2026-11-05T03:41:37.335Z"}""";
    const string Resume   = """{"type":"session.resume","data":{},"id":"e2","timestamp":"2026-11-05T03:41:38.226Z"}""";
    const string Quoted   = """{"type":"user.message","data":{"content":"why \"session.shutdown\"?"},"id":"e3"}""";

    static readonly DateTimeOffset T0 = new(2026, 11, 5, 3, 41, 37, TimeSpan.Zero);

    [TempConfigRoot] public required TempConfigRoot Config { get; init; }
    [TempDir] public required TempDir Tmp { get; init; }

    readonly FakeTimeProvider _time = new(T0);

    TestRecorders Recorders => field ??= new(Config.Root, _time);

    async Task Drain(string path, WatchState state) {
        await Recorders.For(Guid.NewGuid().ToString("N"), path, null, "copilot").DrainNewLines(state, CancellationToken.None);
    }

    [Test]
    public async Task A_real_drain_tracks_the_shutdown_and_a_resume_written_after_it_clears_it() {
        var path  = Tmp.CreateFile("events.jsonl", Resume + "\n" + Shutdown + "\n");
        var state = new WatchState();

        await Drain(path, state);
        await Assert.That(state.CopilotShutdownSeenAt).IsEqualTo(T0);

        _time.Advance(TimeSpan.FromSeconds(5));
        await Drain(path, state);
        await Assert.That(state.CopilotShutdownSeenAt).IsEqualTo(T0);

        await File.AppendAllTextAsync(path, Resume + "\n");
        await Drain(path, state);
        await Assert.That(state.CopilotShutdownSeenAt).IsNull();
    }

    /// <summary>A resumed or forked session can sit on an earlier run's shutdown until its first new line, with its
    /// runtime live.</summary>
    [Test]
    [Arguments(Shutdown, -1, true)]
    [Arguments(Shutdown, 1, false)]
    [Arguments("""{"type":"session.shutdown","data":{},"id":"e4"}""", -1, false)]
    public async Task Only_a_shutdown_written_since_the_watcher_started_counts(string line, int startOffsetSeconds, bool seen) =>
        await Assert.That(CopilotShutdown.CopilotShutdownAfter(null, [line], T0, T0.AddSeconds(startOffsetSeconds)) is not null).IsEqualTo(seen);

    [Test]
    [Arguments("copilot", null, "/home/.copilot/session-state/abc/events.jsonl", true)]
    [Arguments("copilot", "agent-1", "/home/.copilot/session-state/abc/events.jsonl", false)]
    [Arguments("copilot", null, "/data/GitHub.copilot-chat/transcripts/abc.jsonl", false)]
    [Arguments("claude", null, "/home/.claude/projects/abc.jsonl", false)]
    public async Task Only_a_Copilot_session_watcher_on_events_jsonl_tracks_shutdown(string vendor, string? agentId, string transcript, bool expected) =>
        await Assert.That(CopilotShutdown.TracksCopilotShutdown(vendor, agentId, transcript)).IsEqualTo(expected);

    [Test]
    public async Task A_drain_ending_on_shutdown_starts_the_grace_and_blank_lines_do_not_hide_it() =>
        await Assert.That(CopilotShutdown.CopilotShutdownAfter(null, [Resume, Shutdown, "  "], T0, T0)).IsEqualTo(T0);

    [Test]
    [Arguments(Resume)]
    [Arguments(Quoted)]
    public async Task A_later_line_clears_it(string later) =>
        await Assert.That(CopilotShutdown.CopilotShutdownAfter(T0, [Shutdown, later], T0.AddSeconds(1), T0)).IsNull();

    [Test]
    public async Task Nothing_new_and_a_reread_of_the_same_line_keep_the_clock_running() {
        await Assert.That(CopilotShutdown.CopilotShutdownAfter(T0, [], T0.AddSeconds(5), T0)).IsEqualTo(T0);
        await Assert.That(CopilotShutdown.CopilotShutdownAfter(T0, [Shutdown], T0.AddSeconds(5), T0)).IsEqualTo(T0);
    }

    /// <summary>On real transcripts a VS Code resume wrote its line 0.9s after the shutdown.</summary>
    [Test]
    [Arguments(1, false)]
    [Arguments(29, false)]
    [Arguments(30, true)]
    public async Task It_settles_only_after_the_grace(int seconds, bool expected) =>
        await Assert.That(CopilotShutdown.CopilotShutdownSettled(T0, T0.AddSeconds(seconds))).IsEqualTo(expected);

    [Test]
    public async Task No_shutdown_seen_never_settles() =>
        await Assert.That(CopilotShutdown.CopilotShutdownSettled(null, T0.AddDays(1))).IsFalse();
}
