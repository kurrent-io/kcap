namespace Capacitor.Cli.Core.Tests.Unit;

public class DisabledSessionsTests {
    const string Session = "0123456789abcdef0123456789abcdef";
    const string Dashed  = "01234567-89ab-cdef-0123-456789abcdef";

    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    [Test]
    public async Task An_exclusion_quiets_the_hooks_and_is_no_persons_disable() {
        DisabledSessions.Exclude(Session, Config.Root);

        await Assert.That(DisabledSessions.IsDisabled(Session, Config.Root)).IsTrue();
        await Assert.That(DisabledSessions.IsDisabledByPerson(Session, Config.Root)).IsFalse();
    }

    /// <summary>A session's end lifts an exclusion on every vendor, and that end also comes to a disabled session.</summary>
    [Test]
    public async Task Lifting_an_exclusion_leaves_a_persons_disable() {
        DisabledSessions.Exclude(Session, Config.Root);
        DisabledSessions.Mark(Session, Config.Root);

        DisabledSessions.RemoveExclusion(Session, Config.Root);

        await Assert.That(DisabledSessions.IsDisabledByPerson(Session, Config.Root)).IsTrue();
    }

    [Test]
    public async Task A_lifted_exclusion_leaves_the_session_heard_again() {
        DisabledSessions.Exclude(Session, Config.Root);

        DisabledSessions.RemoveExclusion(Session, Config.Root);

        await Assert.That(DisabledSessions.IsDisabled(Session, Config.Root)).IsFalse();
    }

    /// <summary>The stop that follows a mark can be cut short, and a marker left empty for its length would read as clean
    /// to the next run.</summary>
    [Test]
    public async Task Marking_again_keeps_what_an_earlier_disable_recorded() {
        DisabledSessions.Mark(Session, Config.Root);
        DisabledSessions.MarkDeletedUnconfirmed(Session, Config.Root);

        DisabledSessions.Mark(Session, Config.Root);

        await Assert.That(DisabledSessions.WasDeletedUnconfirmed(Session, Config.Root)).IsTrue();
    }

    /// <summary>Another run's record of an unconfirmed deletion can land while this run marks, and the mark must not empty
    /// it. Many rounds, because only one interleaving inside the mark loses it.</summary>
    [Test]
    public async Task A_mark_racing_another_runs_record_keeps_it() {
        for (var round = 0; round < 500; round++) {
            var session = round.ToString("x32", System.Globalization.CultureInfo.InvariantCulture);
            using var start = new Barrier(2);

            await Task.WhenAll(
                Task.Run(() => {
                    start.SignalAndWait();
                    DisabledSessions.Mark(session, Config.Root);
                }),
                Task.Run(() => {
                    start.SignalAndWait();
                    DisabledSessions.MarkDeletedUnconfirmed(session, Config.Root);
                }));

            await Assert.That(DisabledSessions.WasDeletedUnconfirmed(session, Config.Root)).IsTrue();
        }
    }

    /// <summary>A run can leave both facts, in either order, and neither write may cost the other.</summary>
    [Test]
    public async Task Each_fact_a_disable_keeps_survives_the_other() {
        DisabledSessions.Mark(Session, Config.Root);
        DisabledSessions.MarkOnOtherServer(Session, "https://a.example", Config.Root);
        DisabledSessions.MarkDeletedUnconfirmed(Session, Config.Root);
        DisabledSessions.MarkOnOtherServer(Session, "https://b.example", Config.Root);

        await Assert.That(DisabledSessions.WasDeletedUnconfirmed(Session, Config.Root)).IsTrue();
        await Assert.That(DisabledSessions.OtherServers(Session, Config.Root)).IsEquivalentTo(["https://a.example", "https://b.example"]);
        await Assert.That(DisabledSessions.IsDisabledByPerson(Session, Config.Root)).IsTrue();
    }

    /// <summary>The same server under another spelling is the same server, and is named once.</summary>
    [Test]
    public async Task A_server_found_again_is_kept_once() {
        DisabledSessions.MarkOnOtherServer(Session, "https://a.example", Config.Root);
        DisabledSessions.MarkOnOtherServer(Session, "HTTPS://A.EXAMPLE:443/", Config.Root);

        await Assert.That(DisabledSessions.OtherServers(Session, Config.Root)).IsEquivalentTo(["https://a.example"]);
    }

    /// <summary>A spool keeps the spelling of the hook that wrote it, and the person's marker has the canonical one.</summary>
    [Test]
    public async Task A_persons_disable_is_found_under_a_vendors_spelling() {
        DisabledSessions.Mark(Session, Config.Root);

        await Assert.That(DisabledSessions.IsDisabledByPerson(Dashed, Config.Root)).IsTrue();
        await Assert.That(DisabledSessions.IsDisabled(Dashed, Config.Root)).IsTrue();
    }
}
