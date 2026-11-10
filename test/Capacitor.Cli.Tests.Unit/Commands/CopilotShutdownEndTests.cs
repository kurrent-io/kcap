using Capacitor.Cli.Commands;
using Capacitor.Cli.Commands.Harness;
using Capacitor.Cli.Core.LocalEdition;
using Capacitor.Cli.Core;
using Capacitor.Cli.PrDetection;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>A watcher ends a Copilot session itself once <c>session.shutdown</c> stays the transcript's last
/// line: the sessionEnd hook does not always fire, and a resume appends to the same file.</summary>
public class CopilotShutdownEndTests {
    const string Shutdown = """{"type":"session.shutdown","data":{},"id":"e1","timestamp":"2026-11-05T03:41:37.335Z"}""";
    const string Resume   = """{"type":"session.resume","data":{},"id":"e2","timestamp":"2026-11-05T03:41:38.226Z"}""";
    const string Quoted   = """{"type":"user.message","data":{"content":"why \"session.shutdown\"?"},"id":"e3"}""";

    static readonly DateTimeOffset T0 = new(2026, 11, 5, 3, 41, 37, TimeSpan.Zero);

    [TempHome] public required TempHome Home { get; init; }
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }
    [TempDir] public required TempDir Tmp { get; init; }

    readonly FakeTimeProvider _time = new(T0);

    WatchCommand Watch => field ??= new(Config.Root, Resolutions.None(Config.Root), TestHarnesses.Under(Home), new FixedCapacitorHttpClient(), new FixedCredentialSource(), TestWatchers.For(Config.Root, Resolutions.None(Config.Root), new FixedCapacitorHttpClient()), new GitProviderRouter(), _time, TestAccounts.None, Home, LocalIntelligenceGate.For(Config.Root, NoLocalInstallation.Root));

    async Task Drain(string path, WatchState state) {
        await using var hub = new HubConnectionBuilder().WithUrl("http://127.0.0.1:1/hubs/sessions").Build();
        await Watch.DrainNewLines(hub, Guid.NewGuid().ToString("N"), path, null, state, "copilot", CancellationToken.None);
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
        await Assert.That(WatchCommand.CopilotShutdownAfter(null, [line], T0, T0.AddSeconds(startOffsetSeconds)) is not null).IsEqualTo(seen);

    [Test]
    [Arguments("copilot", null, "/home/.copilot/session-state/abc/events.jsonl", true)]
    [Arguments("copilot", "agent-1", "/home/.copilot/session-state/abc/events.jsonl", false)]
    [Arguments("copilot", null, "/data/GitHub.copilot-chat/transcripts/abc.jsonl", false)]
    [Arguments("claude", null, "/home/.claude/projects/abc.jsonl", false)]
    public async Task Only_a_Copilot_session_watcher_on_events_jsonl_tracks_shutdown(string vendor, string? agentId, string transcript, bool expected) =>
        await Assert.That(WatchCommand.TracksCopilotShutdown(vendor, agentId, transcript)).IsEqualTo(expected);

    [Test]
    public async Task A_drain_ending_on_shutdown_starts_the_grace_and_blank_lines_do_not_hide_it() =>
        await Assert.That(WatchCommand.CopilotShutdownAfter(null, [Resume, Shutdown, "  "], T0, T0)).IsEqualTo(T0);

    [Test]
    [Arguments(Resume)]
    [Arguments(Quoted)]
    public async Task A_later_line_clears_it(string later) =>
        await Assert.That(WatchCommand.CopilotShutdownAfter(T0, [Shutdown, later], T0.AddSeconds(1), T0)).IsNull();

    [Test]
    public async Task Nothing_new_and_a_reread_of_the_same_line_keep_the_clock_running() {
        await Assert.That(WatchCommand.CopilotShutdownAfter(T0, [], T0.AddSeconds(5), T0)).IsEqualTo(T0);
        await Assert.That(WatchCommand.CopilotShutdownAfter(T0, [Shutdown], T0.AddSeconds(5), T0)).IsEqualTo(T0);
    }

    /// <summary>On real transcripts a VS Code resume wrote its line 0.9s after the shutdown.</summary>
    [Test]
    [Arguments(1, false)]
    [Arguments(29, false)]
    [Arguments(30, true)]
    public async Task It_settles_only_after_the_grace(int seconds, bool expected) =>
        await Assert.That(WatchCommand.CopilotShutdownSettled(T0, T0.AddSeconds(seconds))).IsEqualTo(expected);

    /// <summary>A transcript that can't be read must not cancel an end already decided.</summary>
    [Test]
    [Arguments(Shutdown, false)]
    [Arguments(Resume, true)]
    [Arguments(null, false)]
    public async Task Only_a_readable_later_line_moves_past_the_shutdown(string? last, bool moved) {
        var path = last is null ? Tmp.PathTo("missing.jsonl") : Tmp.CreateFile("events.jsonl", Shutdown + "\n" + last + "\n");

        await Assert.That(CopilotFinalizeDrainCommand.MovedPastShutdown(path)).IsEqualTo(moved);
    }

    [Test]
    public async Task No_shutdown_seen_never_settles() =>
        await Assert.That(WatchCommand.CopilotShutdownSettled(null, T0.AddDays(1))).IsFalse();

    [Test]
    public async Task A_settled_shutdown_ends_the_session_and_the_process_exiting_outranks_it() {
        await Assert.That(WatchCommand.DecideEndReason(false, false, false, false, copilotShutdown: true)).IsEqualTo("session_shutdown");
        await Assert.That(WatchCommand.DecideEndReason(true, false, false, false, copilotShutdown: true)).IsEqualTo("parent_exited");
    }

    /// <summary>A Copilot parent is found by name, so finding it dead means the runtime has stopped. Other vendors
    /// can be following a hook shell here, and wait for a re-resolve instead.</summary>
    [Test]
    [Arguments("copilot", null, true)]
    [Arguments("copilot", "agent-1", false)]
    [Arguments("kiro", null, false)]
    public async Task A_dead_Copilot_parent_ends_the_session_without_waiting(string vendor, string? agentId, bool expected) =>
        await Assert.That(WatchCommand.EndsWhenParentFoundDead(vendor, agentId)).IsEqualTo(expected);
}
