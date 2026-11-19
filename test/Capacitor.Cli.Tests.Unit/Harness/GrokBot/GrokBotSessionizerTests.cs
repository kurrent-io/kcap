using Capacitor.Cli.Harness.GrokBot;

namespace Capacitor.Cli.Tests.Unit.Harness.GrokBot;

/// <summary>Pins how a Bot's endless thread is cut into sessions and when an entry may be sent.</summary>
public class GrokBotSessionizerTests {
    const string Agent = "4d30ec7f-1027-4a23-86d7-d70f37e5bfec";
    static readonly TimeSpan Gap = TimeSpan.FromHours(2);
    const long Hour = 3_600_000;
    const long T0   = 1_790_000_000_000;

    static GrokBotEntry Entry(int seq, long ts, bool streaming = false, bool awaiting = false, bool startsTurn = false) =>
        new($"e{seq}", seq, ts, streaming, awaiting, startsTurn, $$"""{"id":"e{{seq}}","seq":{{seq}}}""");

    static IReadOnlyList<GrokBotAction> Plan(GrokBotBotState state, long now, bool running = false, params GrokBotEntry[] entries) =>
        GrokBotSessionizer.Plan(Agent, state, entries, now, running, Gap);

    [Test]
    public async Task First_entries_open_a_session_and_send_them_in_seq_order() {
        var actions = Plan(GrokBotBotState.Empty, T0 + 10, entries: [Entry(5, T0 + 5), Entry(3, T0)]);

        await Assert.That(actions.Count).IsEqualTo(2);
        var start = (GrokBotStartSession)actions[0];
        var send  = (GrokBotSendLines)actions[1];

        await Assert.That(start.SessionId).IsEqualTo(GrokBotSessionizer.SessionId(Agent, "e3"));
        await Assert.That(start.StartedAtMs).IsEqualTo(T0);
        await Assert.That(send.Seqs).IsEquivalentTo([3, 5]);
        await Assert.That(send.After.LastSeq).IsEqualTo(5);
        await Assert.That(send.After.Open!.LastEntryMs).IsEqualTo(T0 + 5);
    }

    [Test]
    public async Task Start_state_does_not_advance_the_seq_so_a_failed_send_is_retried() {
        var start = (GrokBotStartSession)Plan(GrokBotBotState.Empty, T0, entries: [Entry(1, T0)])[0];

        await Assert.That(start.After.LastSeq).IsEqualTo(-1);
        await Assert.That(start.After.Open).IsNotNull();
    }

    [Test]
    public async Task An_entry_after_the_gap_ends_the_open_session_and_opens_another() {
        var first = Plan(GrokBotBotState.Empty, T0, entries: [Entry(1, T0)])[^1].After;

        var actions = Plan(first, T0 + 3 * Hour, entries: [Entry(2, T0 + 3 * Hour)]);

        await Assert.That(actions.Select(a => a.GetType().Name))
            .IsEquivalentTo([nameof(GrokBotEndSession), nameof(GrokBotStartSession), nameof(GrokBotSendLines)]);
        var end = (GrokBotEndSession)actions[0];
        await Assert.That(end.SessionId).IsEqualTo(first.Open!.SessionId);
        await Assert.That(end.EndedAtMs).IsEqualTo(T0);
        await Assert.That(((GrokBotStartSession)actions[1]).SessionId).IsEqualTo(GrokBotSessionizer.SessionId(Agent, "e2"));
    }

    [Test]
    public async Task Entries_inside_the_gap_stay_in_one_session() {
        var actions = Plan(GrokBotBotState.Empty, T0 + Hour, entries: [Entry(1, T0), Entry(2, T0 + Hour)]);

        await Assert.That(actions.OfType<GrokBotStartSession>().Count()).IsEqualTo(1);
        await Assert.That(actions.OfType<GrokBotEndSession>()).IsEmpty();
    }

    [Test]
    public async Task An_idle_thread_ends_its_session_at_the_last_entry_once_the_gap_passes() {
        var open = Plan(GrokBotBotState.Empty, T0, entries: [Entry(1, T0)])[^1].After;

        await Assert.That(Plan(open, T0 + Hour)).IsEmpty();

        var end = (GrokBotEndSession)Plan(open, T0 + 2 * Hour)[0];
        await Assert.That(end.EndedAtMs).IsEqualTo(T0);
        await Assert.That(end.After.Open).IsNull();
        await Assert.That(end.After.LastSeq).IsEqualTo(1);
    }

    [Test]
    public async Task A_running_turn_keeps_an_idle_session_open() {
        var open = Plan(GrokBotBotState.Empty, T0, entries: [Entry(1, T0)])[^1].After;

        await Assert.That(Plan(open, T0 + 3 * Hour, running: true)).IsEmpty();
    }

    [Test]
    public async Task A_streaming_entry_holds_back_itself_and_everything_after_it() {
        var actions = Plan(GrokBotBotState.Empty, T0 + 2, entries: [Entry(1, T0), Entry(2, T0 + 1, streaming: true), Entry(3, T0 + 2)]);

        await Assert.That(((GrokBotSendLines)actions[^1]).Seqs).IsEquivalentTo([1]);
    }

    /// <summary>The answer lands on the question's own entry, and the server keeps the first copy of a line:
    /// a question sent while the Bot is still talking would lose the answer the user gives next.</summary>
    [Test]
    public async Task An_unanswered_question_waits_through_later_bot_entries_until_the_user_moves_on() {
        var waiting = Plan(GrokBotBotState.Empty, T0 + 1, entries: [Entry(1, T0), Entry(2, T0 + 1, awaiting: true)]);
        await Assert.That(((GrokBotSendLines)waiting[^1]).Seqs).IsEquivalentTo([1]);

        var botWentOn = Plan(GrokBotBotState.Empty, T0 + 2, entries: [Entry(1, T0), Entry(2, T0 + 1, awaiting: true), Entry(3, T0 + 2)]);
        await Assert.That(((GrokBotSendLines)botWentOn[^1]).Seqs).IsEquivalentTo([1]);

        var userMovedOn = Plan(GrokBotBotState.Empty, T0 + 3, entries: [Entry(1, T0), Entry(2, T0 + 1, awaiting: true), Entry(3, T0 + 2), Entry(4, T0 + 3, startsTurn: true)]);
        await Assert.That(((GrokBotSendLines)userMovedOn[^1]).Seqs).IsEquivalentTo([1, 2, 3, 4]);
    }

    /// <summary>The limit of waiting: past the gap the question goes as it stands, and an answer given after
    /// that has no line left to arrive on.</summary>
    [Test]
    public async Task An_unsettled_entry_older_than_the_gap_is_sent_as_it_stands() {
        var actions = Plan(GrokBotBotState.Empty, T0 + 3 * Hour, entries: [Entry(1, T0, awaiting: true)]);

        await Assert.That(actions.OfType<GrokBotSendLines>().Single().Seqs).IsEquivalentTo([1]);
        await Assert.That(actions[^1]).IsTypeOf<GrokBotEndSession>();
    }

    [Test]
    public async Task A_session_stays_open_for_a_held_entry_that_belongs_to_it() {
        var open = Plan(GrokBotBotState.Empty, T0, entries: [Entry(1, T0)])[^1].After;
        var held = Entry(2, T0 + Hour, awaiting: true);

        await Assert.That(Plan(open, T0 + 2 * Hour, entries: [held])).IsEmpty();

        var settled = Plan(open, T0 + 2 * Hour + 1, entries: [held with { IsAwaitingAnswer = false }]);
        await Assert.That(settled.OfType<GrokBotStartSession>()).IsEmpty();
        await Assert.That(((GrokBotSendLines)settled[0]).SessionId).IsEqualTo(open.Open!.SessionId);
    }

    [Test]
    public async Task A_held_entry_a_gap_after_the_session_does_not_keep_it_open() {
        var open = Plan(GrokBotBotState.Empty, T0, entries: [Entry(1, T0)])[^1].After;

        var actions = Plan(open, T0 + 3 * Hour, entries: [Entry(2, T0 + 3 * Hour - 1, awaiting: true)]);

        await Assert.That(actions.Single()).IsTypeOf<GrokBotEndSession>();
    }

    [Test]
    public async Task Entries_already_sent_are_never_planned_again() {
        var sent = Plan(GrokBotBotState.Empty, T0 + 1, entries: [Entry(1, T0), Entry(2, T0 + 1)])[^1].After;

        await Assert.That(Plan(sent, T0 + 1, entries: [Entry(1, T0), Entry(2, T0 + 1)])).IsEmpty();
    }

    [Test]
    public async Task Long_runs_are_split_into_batches_each_carrying_its_own_progress() {
        var entries = Enumerable.Range(1, 250).Select(i => Entry(i, T0 + i)).ToArray();

        var batches = GrokBotSessionizer.Plan(Agent, GrokBotBotState.Empty, entries, T0 + 250, false, Gap)
            .OfType<GrokBotSendLines>().ToList();

        await Assert.That(batches.Select(b => b.Lines.Length)).IsEquivalentTo([100, 100, 50]);
        await Assert.That(batches.Select(b => b.After.LastSeq)).IsEquivalentTo([100, 200, 250]);
    }

    [Test]
    public async Task A_chat_first_seen_without_its_beginning_waits_for_a_quiet_gap() {
        GrokBotEntry[] midSession = [Entry(40, T0), Entry(41, T0 + 1)];

        await Assert.That(GrokBotSessionizer.Plan(Agent, GrokBotBotState.Empty, midSession, T0 + 2, false, Gap, fromThreadStart: false)).IsEmpty();
    }

    [Test]
    public async Task A_chat_first_seen_without_its_beginning_is_recorded_from_its_first_quiet_gap() {
        GrokBotEntry[] entries = [Entry(40, T0), Entry(41, T0 + 1), Entry(42, T0 + 3 * Hour), Entry(43, T0 + 3 * Hour + 1)];

        var actions = GrokBotSessionizer.Plan(Agent, GrokBotBotState.Empty, entries, T0 + 3 * Hour + 2, false, Gap, fromThreadStart: false);

        await Assert.That(((GrokBotStartSession)actions[0]).SessionId).IsEqualTo(GrokBotSessionizer.SessionId(Agent, "e42"));
        await Assert.That(((GrokBotSendLines)actions[1]).Seqs).IsEquivalentTo([42, 43]);
    }

    /// <summary>The start was accepted and the send refused, so nothing is delivered yet and the session is
    /// open: the retry must begin at the entry that names the session, not at the head it passed over.</summary>
    [Test]
    public async Task A_retry_after_an_accepted_start_sends_from_the_entry_the_session_opened_at() {
        GrokBotEntry[] entries = [Entry(40, T0), Entry(41, T0 + 3 * Hour), Entry(42, T0 + 3 * Hour + 1)];
        var started = (GrokBotStartSession)GrokBotSessionizer.Plan(Agent, GrokBotBotState.Empty, entries, T0 + 3 * Hour + 2, false, Gap, fromThreadStart: false)[0];

        await Assert.That(started.After.LastSeq).IsEqualTo(-1);

        foreach (var fromStart in new[] { false, true }) {
            var retry = GrokBotSessionizer.Plan(Agent, started.After, entries, T0 + 3 * Hour + 2, false, Gap, fromThreadStart: fromStart);

            await Assert.That(retry.OfType<GrokBotStartSession>()).IsEmpty();
            await Assert.That(((GrokBotSendLines)retry[0]).SessionId).IsEqualTo(started.SessionId);
            await Assert.That(((GrokBotSendLines)retry[0]).Seqs).IsEquivalentTo([41, 42]);
        }
    }

    [Test]
    public async Task A_chat_already_being_recorded_ignores_a_read_without_its_beginning() {
        var recorded = new GrokBotBotState(41, null);

        var actions = GrokBotSessionizer.Plan(Agent, recorded, [Entry(42, T0)], T0 + 1, false, Gap, fromThreadStart: false);

        await Assert.That(actions.OfType<GrokBotSendLines>().Single().Seqs).IsEquivalentTo([42]);
    }

    [Test]
    public async Task Session_ids_are_32_lowercase_hex_and_stable() {
        var id = GrokBotSessionizer.SessionId(Agent, "t2u");

        await Assert.That(id).IsEqualTo(GrokBotSessionizer.SessionId(Agent, "t2u"));
        await Assert.That(id.Length).IsEqualTo(32);
        await Assert.That(id.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f')).IsTrue();
        await Assert.That(GrokBotSessionizer.SessionId("other", "t2u")).IsNotEqualTo(id);
    }
}
