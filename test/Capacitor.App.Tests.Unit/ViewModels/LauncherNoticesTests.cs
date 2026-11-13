using System.Reactive.Subjects;
using Capacitor.App.ViewModels;
using static Capacitor.App.ViewModels.LauncherNotices.Kind;

namespace Capacitor.App.Tests.Unit.ViewModels;

[NotInParallel("AvaloniaSession")]
public class LauncherNoticesTests {
    static readonly (bool, LauncherNotices.Kind) Off = (false, Problem);

    sealed class Rig : IDisposable {
        public readonly BehaviorSubject<(bool, LauncherNotices.Kind)> Stack = new(Off);
        public readonly BehaviorSubject<(bool, LauncherNotices.Kind)> Server = new(Off);
        public readonly BehaviorSubject<(bool, LauncherNotices.Kind)> Connection = new(Off);
        public readonly BehaviorSubject<(bool, LauncherNotices.Kind)> History = new(Off);
        public readonly BehaviorSubject<bool> Update = new(false);
        public readonly LauncherNotices Notices = new();

        public Rig() {
            Notices.TrackStack(Stack);
            Notices.TrackServer(Server);
            Notices.TrackConnection(Connection);
            Notices.TrackUpdate(Update);
            Notices.TrackHistory(History);
        }

        public (bool Stack, bool Server, bool Connection) Shown =>
            (Notices.ShowStack, Notices.ShowServer, Notices.ShowConnection);

        public (bool Stack, bool Server, bool Connection, bool Update, bool History) AllShown =>
            (Notices.ShowStack, Notices.ShowServer, Notices.ShowConnection, Notices.ShowUpdate, Notices.ShowHistory);

        public void Dispose() => Notices.Dispose();
    }

    /// While an update is finishing its card is the only one, even over a problem: the others describe what the update is doing.
    [Test]
    public async Task A_finishing_update_stands_in_for_every_other_card() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var rig = new Rig();
            rig.Server.OnNext((true, Problem));
            rig.Connection.OnNext((true, Progress));

            rig.Update.OnNext(true);
            await Assert.That(rig.Notices.ShowUpdate).IsTrue();
            await Assert.That(rig.Shown).IsEqualTo((false, false, false));

            rig.Update.OnNext(false);
            await Assert.That(rig.Notices.ShowUpdate).IsFalse();
            await Assert.That(rig.Shown).IsEqualTo((false, true, false));
        });
    }

    /// Only one card shows, and the next takes its place once it clears.
    [Test]
    public async Task One_card_shows_and_the_next_follows_when_it_clears() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var rig = new Rig();
            await Assert.That(rig.Shown).IsEqualTo((false, false, false));

            rig.Server.OnNext((true, Problem));
            rig.Connection.OnNext((true, Problem));
            await Assert.That(rig.Shown).IsEqualTo((false, true, false));

            rig.Server.OnNext(Off);
            await Assert.That(rig.Shown).IsEqualTo((false, false, true));
        });
    }

    /// Something the user can act on outranks a card that is only reporting progress or a note, even
    /// from a later layer; among equals the earlier layer leads.
    [Test]
    [Arguments(Progress, Problem, false, true)]
    [Arguments(Note, Problem, false, true)]
    [Arguments(Problem, Problem, true, false)]
    [Arguments(Problem, Progress, true, false)]
    public async Task An_actionable_card_outranks_progress_and_notes(
            LauncherNotices.Kind stack, LauncherNotices.Kind connection, bool stackShown, bool connectionShown) {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var rig = new Rig();
            rig.Stack.OnNext((true, stack));
            rig.Connection.OnNext((true, connection));

            await Assert.That(rig.Shown).IsEqualTo((stackShown, false, connectionShown));
        });
    }

    /// A local stack card that is not a note already covers the server, so the server card folds into it;
    /// behind a note, the server card is a problem of its own and outranks it.
    [Test]
    [Arguments(Problem, false)]
    [Arguments(Progress, false)]
    [Arguments(Note, true)]
    public async Task A_stack_card_absorbs_the_local_server_card(LauncherNotices.Kind stack, bool serverShown) {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var rig = new Rig();
            rig.Stack.OnNext((true, stack));
            rig.Server.OnNext((true, Problem));

            await Assert.That(rig.Shown).IsEqualTo((!serverShown, serverShown, false));
        });
    }

    /// Import is last among equals. An unfinished import needs attention without obscuring
    /// infrastructure progress, while running imports and attention both outrank passive notes.
    [Test]
    [Arguments(Progress, Problem, false)]
    [Arguments(Progress, Progress, false)]
    [Arguments(Progress, Note, true)]
    [Arguments(Attention, Problem, false)]
    [Arguments(Attention, Progress, false)]
    [Arguments(Attention, Attention, false)]
    [Arguments(Attention, Note, true)]
    [Arguments(Note, Problem, false)]
    [Arguments(Note, Progress, false)]
    [Arguments(Note, Note, false)]
    public async Task History_priority_applies_to_each_infrastructure_source(
            LauncherNotices.Kind historyKind, LauncherNotices.Kind infrastructureKind, bool historyShown) {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            for (var slot = 0; slot < 3; slot++) {
                using var rig = new Rig();
                var system = slot switch { 0 => rig.Stack, 1 => rig.Server, _ => rig.Connection };

                rig.History.OnNext((true, historyKind));
                await Assert.That(rig.AllShown).IsEqualTo((false, false, false, false, true));

                system.OnNext((true, infrastructureKind));
                await Assert.That(rig.AllShown).IsEqualTo((
                    slot == 0 && !historyShown,
                    slot == 1 && !historyShown,
                    slot == 2 && !historyShown,
                    false, historyShown));

                system.OnNext(Off);
                await Assert.That(rig.AllShown).IsEqualTo((false, false, false, false, true));
            }
        });
    }

    [Test]
    [Arguments(Progress)]
    [Arguments(Attention)]
    [Arguments(Note)]
    public async Task A_finishing_update_preempts_every_history_kind_then_reveals_the_next_notice(
            LauncherNotices.Kind historyKind) {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var rig = new Rig();
            rig.History.OnNext((true, historyKind));
            rig.Update.OnNext(true);
            rig.Stack.OnNext((true, Problem));
            rig.Server.OnNext((true, Problem));
            rig.Connection.OnNext((true, Progress));
            await Assert.That(rig.AllShown).IsEqualTo((false, false, false, true, false));

            rig.Update.OnNext(false);
            await Assert.That(rig.AllShown).IsEqualTo((true, false, false, false, false));

            rig.Stack.OnNext(Off);
            await Assert.That(rig.AllShown).IsEqualTo((false, true, false, false, false));

            rig.Server.OnNext(Off);
            await Assert.That(rig.AllShown).IsEqualTo((false, false, true, false, false));

            rig.Connection.OnNext(Off);
            await Assert.That(rig.AllShown).IsEqualTo((false, false, false, false, true));
        });
    }

    /// A hidden run can fail or finish before infrastructure recovers. Selection must use that
    /// latest kind, rather than queueing the running notice that was first preempted.
    [Test]
    [Arguments(Attention, true)]
    [Arguments(Note, false)]
    public async Task Hidden_history_keeps_its_latest_state(LauncherNotices.Kind latestKind, bool historyShown) {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var rig = new Rig();
            rig.History.OnNext((true, Progress));
            rig.Connection.OnNext((true, Progress));
            rig.Stack.OnNext((true, Note));
            await Assert.That(rig.AllShown).IsEqualTo((false, false, true, false, false));

            rig.History.OnNext((true, latestKind));
            await Assert.That(rig.AllShown).IsEqualTo((false, false, true, false, false));

            rig.Connection.OnNext(Off);
            await Assert.That(rig.AllShown).IsEqualTo((!historyShown, false, false, false, historyShown));

            rig.Stack.OnNext(Off);
            await Assert.That(rig.AllShown).IsEqualTo((false, false, false, false, true));
        });
    }

    /// Dismissal belongs to the run's visibility feed. Changes to a withdrawn candidate must not
    /// resurrect it; a genuinely new candidate can still show and reveal the next notice on dismissal.
    [Test]
    public async Task Withdrawn_history_stays_off_until_a_new_candidate_arrives() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var rig = new Rig();
            await Assert.That(rig.AllShown).IsEqualTo((false, false, false, false, false));

            rig.History.OnNext((true, Progress));
            await Assert.That(rig.AllShown).IsEqualTo((false, false, false, false, true));

            rig.History.OnNext((false, Progress));
            rig.History.OnNext((false, Attention));
            rig.History.OnNext((false, Note));
            rig.Connection.OnNext((true, Problem));
            rig.Connection.OnNext(Off);
            await Assert.That(rig.AllShown).IsEqualTo((false, false, false, false, false));

            rig.Stack.OnNext((true, Note));
            rig.Update.OnNext(true);
            await Assert.That(rig.AllShown).IsEqualTo((false, false, false, true, false));
            rig.Update.OnNext(false);
            await Assert.That(rig.AllShown).IsEqualTo((true, false, false, false, false));

            rig.History.OnNext((true, Progress));
            await Assert.That(rig.AllShown).IsEqualTo((false, false, false, false, true));
            rig.History.OnNext((false, Progress));
            await Assert.That(rig.AllShown).IsEqualTo((true, false, false, false, false));
        });
    }

    [Test]
    public async Task Import_attention_does_not_disrupt_stack_server_absorption() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var rig = new Rig();
            rig.History.OnNext((true, Attention));
            rig.Server.OnNext((true, Problem));
            rig.Stack.OnNext((true, Progress));
            await Assert.That(rig.AllShown).IsEqualTo((true, false, false, false, false));

            rig.Stack.OnNext(Off);
            await Assert.That(rig.AllShown).IsEqualTo((false, true, false, false, false));

            rig.Server.OnNext(Off);
            await Assert.That(rig.AllShown).IsEqualTo((false, false, false, false, true));
        });
    }
    [Test]
    public async Task Replacing_and_releasing_history_ownership_cannot_clear_a_newer_owner() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var notices = new LauncherNotices();
            using var old = new BehaviorSubject<(bool, LauncherNotices.Kind)>((true, Progress));
            using var current = new BehaviorSubject<(bool, LauncherNotices.Kind)>((true, Attention));
            var oldLease = notices.TrackHistory(old);
            var currentLease = notices.TrackHistory(current);
            oldLease.Dispose();
            old.OnNext(Off);
            await Assert.That(notices.ShowHistory).IsTrue();
            currentLease.Dispose();
            await Assert.That(notices.ShowHistory).IsFalse();
            notices.Dispose();
            notices.TrackHistory(current).Dispose();
            current.OnNext((true, Note));
            await Assert.That(notices.ShowHistory).IsFalse();
        });
    }

}
