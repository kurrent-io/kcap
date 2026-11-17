using Capacitor.App.ViewModels;
using Capacitor.Cli.Core;
using DynamicData;
using static Capacitor.App.Tests.Unit.AvaloniaSession;
using static Capacitor.App.Tests.Unit.WorkspaceFixtures;

namespace Capacitor.App.Tests.Unit.ViewModels;

public partial class ChatTabViewModelTests {
    /// Busy state comes from the daemon, so the note remains visible before and during output.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Activity_note_reads_starting_then_working_and_clears_when_the_agent_waits() {
        await RunOnUiAsync(async () => {
            var path = Tmp.CreateFile("j.jsonl", [
                EnvelopeJournalFormat.Write(new AcpEventEnvelope(Kind: AcpEventKind.SessionStarted, Cwd: "/w")),
            ]);
            var h = new Harness(TranscriptChat.Journal);

            await h.PushAsync(Hosted(path, "Starting", awaitingInput: false));
            await h.TickAsync();
            await Assert.That(h.Chat.Phase).IsEqualTo(ChatTabPhase.Reading);
            await Assert.That(h.Chat.ActivityNote).IsEqualTo("Starting Pi…");

            await h.PushAsync(Hosted(path, "Running", awaitingInput: false));
            await Assert.That(h.Chat.ActivityNote).IsEqualTo("Working for 0s");

            File.AppendAllText(path, EnvelopeJournalFormat.Write(new AcpEventEnvelope(Kind: AcpEventKind.UserMessage, Text: "hi")) + "\n");
            await h.TickAsync();
            await Assert.That(h.Chat.ActivityNote).StartsWith("Working for ");

            // The awaiting-input flag hides it (the turn ended and the agent waits on the user)…
            await h.PushAsync(Hosted(path, "Running", awaitingInput: true));
            await Assert.That(h.Chat.ActivityNote).IsEqualTo("");
            await h.PushAsync(Hosted(path, "Running", awaitingInput: false));
            await Assert.That(h.Chat.ActivityNote).IsEqualTo("Working for 0s");

            // Output does not end the timer; only a turn verdict does.
            File.AppendAllText(path, EnvelopeJournalFormat.Write(new AcpEventEnvelope(Kind: AcpEventKind.AssistantText, Text: "on it")) + "\n");
            await h.TickAsync();
            await Assert.That(h.Chat.ActivityNote).StartsWith("Working for ");
            await h.TeardownAsync();
        });
    }

    /// Navigating away and back builds a new pane mid-turn; it shows the turn's real elapsed time.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_pane_opened_mid_turn_counts_from_the_daemons_turn_start() {
        await RunOnUiAsync(async () => {
            var h = new Harness(TranscriptChat.Journal);
            var started = h.Time.GetUtcNow().UtcDateTime - TimeSpan.FromSeconds(65);
            var dto = Agent("a1", "pi", hasTerminal: false) with { Status = "Running", AwaitingInput = false, WorkingSince = started };
            await h.PushAsync(dto);
            await Assert.That(h.Chat.ActivityNote).IsEqualTo("Working for 1m 05s");
            h.Time.Advance(TimeSpan.FromSeconds(10));
            await h.PushAsync(dto);
            await Assert.That(h.Chat.ActivityNote).IsEqualTo("Working for 1m 15s");
            await h.TeardownAsync();
        });
    }

    /// A pane opened behind a pending card still excludes the time the card stays up.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_pane_opened_behind_a_card_excludes_the_blocked_time() {
        await RunOnUiAsync(async () => {
            var h = new Harness(TranscriptChat.Journal);
            try {
                h.Permissions.Add(PermissionEntries.Entry("r1", "a1"));
                var started = h.Time.GetUtcNow().UtcDateTime - TimeSpan.FromSeconds(65);
                await h.PushAsync(Agent("a1", "pi", hasTerminal: false) with { Status = "Running", AwaitingInput = false, WorkingSince = started });
                await WaitUntilAsync(() => h.Chat.HasPendingCards, what: "the blocking card");
                await Assert.That(h.Chat.ActivityNote).IsEqualTo("");

                h.Time.Advance(TimeSpan.FromMinutes(10));
                h.Permissions.Remove("r1");
                await WaitUntilAsync(() => !h.Chat.HasPendingCards, what: "the card removed");
                await Assert.That(h.Chat.ActivityNote).IsEqualTo("Working for 1m 05s");
            } finally { await h.TeardownAsync(); }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Working_time_ticks_without_a_transcript_and_resets_for_each_turn() {
        await RunOnUiAsync(async () => {
            var h = new Harness(TranscriptChat.Journal);
            var dto = Agent("a1", "pi", hasTerminal: false) with { Status = "Running", AwaitingInput = false };
            await h.PushAsync(dto);
            h.Time.Advance(TimeSpan.FromSeconds(65));
            await Assert.That(h.Chat.ActivityNote).IsEqualTo("Working for 1m 05s");
            await h.PushAsync(dto); // repeated snapshots must not restart the clock
            await Assert.That(h.Chat.ActivityNote).IsEqualTo("Working for 1m 05s");
            await h.PushAsync(dto with { AwaitingInput = true });
            await Assert.That(h.Chat.ActivityNote).IsEqualTo("");
            h.Time.Advance(TimeSpan.FromSeconds(30));
            await h.PushAsync(dto);
            await Assert.That(h.Chat.ActivityNote).IsEqualTo("Working for 0s");
            await h.PushAsync(dto with { AwaitingInput = null });
            await Assert.That(h.Chat.ActivityNote).IsEqualTo("");
            await h.PushAsync(dto);
            h.Daemon.Agents.Remove("a1");
            await Assert.That(h.Chat.ActivityNote).IsEqualTo("");
            await h.TeardownAsync();
            h.Time.Advance(TimeSpan.FromSeconds(5));
            await Assert.That(h.Chat.ActivityNote).IsEqualTo("");
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_working_clock_excludes_time_blocked_on_a_card() {
        await RunOnUiAsync(async () => {
            var h = new Harness(TranscriptChat.Journal);
            try {
                await h.PushAsync(Agent("a1", "pi", hasTerminal: false) with { Status = "Running", AwaitingInput = false });
                h.Time.Advance(TimeSpan.FromSeconds(65));
                h.Permissions.Add(PermissionEntries.Entry("r1", "a1"));
                await WaitUntilAsync(() => h.Chat.HasPendingCards, what: "the blocking card");
                h.Time.Advance(TimeSpan.FromMinutes(10));
                await Assert.That(h.Chat.ActivityNote).IsEqualTo("");
                h.Permissions.Remove("r1");
                await WaitUntilAsync(() => !h.Chat.HasPendingCards, what: "the card removed");
                await Assert.That(h.Chat.ActivityNote).IsEqualTo("Working for 1m 05s");
                h.Time.Advance(TimeSpan.FromSeconds(2));
                await Assert.That(h.Chat.ActivityNote).IsEqualTo("Working for 1m 07s");
            } finally { await h.TeardownAsync(); }
        });
    }

    /// The daemon reports the parent waiting while its subagents still run; the note follows the
    /// count and clears when it drops to zero or is unknown.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_working_note_holds_while_only_subagents_run() {
        await RunOnUiAsync(async () => {
            var h = new Harness(TranscriptChat.Journal);
            try {
                var waiting = Agent("a1", "pi", hasTerminal: false) with { Status = "Running", AwaitingInput = true };
                await h.PushAsync(waiting with { LiveSubagents = 2 });
                await Assert.That(h.Chat.ActivityNote).IsEqualTo("Working for 0s");
                h.Time.Advance(TimeSpan.FromSeconds(65));
                await Assert.That(h.Chat.ActivityNote).IsEqualTo("Working for 1m 05s");

                await h.PushAsync(waiting with { LiveSubagents = 0 });
                await Assert.That(h.Chat.ActivityNote).IsEqualTo("");

                await h.PushAsync(waiting with { LiveSubagents = 1 });
                await Assert.That(h.Chat.ActivityNote).IsEqualTo("Working for 0s");

                await h.PushAsync(waiting with { LiveSubagents = null });
                await Assert.That(h.Chat.ActivityNote).IsEqualTo("");
            } finally { await h.TeardownAsync(); }
        });
    }

    /// A card up means something is blocked on the user, and the note cannot know whether the
    /// asker is the parent or a subagent: the pause applies to background work too.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_pending_card_pauses_the_note_while_subagents_run() {
        await RunOnUiAsync(async () => {
            var h = new Harness(TranscriptChat.Journal);
            try {
                await h.PushAsync(Agent("a1", "pi", hasTerminal: false) with { Status = "Running", AwaitingInput = true, LiveSubagents = 1 });
                h.Time.Advance(TimeSpan.FromSeconds(65));
                await Assert.That(h.Chat.ActivityNote).IsEqualTo("Working for 1m 05s");

                h.Permissions.Add(PermissionEntries.Entry("r1", "a1"));
                await WaitUntilAsync(() => h.Chat.HasPendingCards, what: "the blocking card");
                h.Time.Advance(TimeSpan.FromMinutes(10));
                await Assert.That(h.Chat.ActivityNote).IsEqualTo("");

                h.Permissions.Remove("r1");
                await WaitUntilAsync(() => !h.Chat.HasPendingCards, what: "the card removed");
                await Assert.That(h.Chat.ActivityNote).IsEqualTo("Working for 1m 05s");
                h.Time.Advance(TimeSpan.FromSeconds(2));
                await Assert.That(h.Chat.ActivityNote).IsEqualTo("Working for 1m 07s");
            } finally { await h.TeardownAsync(); }
        });
    }
}
