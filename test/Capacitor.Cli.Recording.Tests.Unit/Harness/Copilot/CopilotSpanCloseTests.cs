using Capacitor.Cli.Core;
using Capacitor.Cli.Recording.Harness.Copilot;

namespace Capacitor.Cli.Recording.Tests.Unit.Harness.Copilot;

/// <summary>A spooled Copilot end owes its session a span close on its server. Only that end's delivery makes the close
/// due; an end that left the spool any other way, superseded by a resumed run or never spooled, takes the debt with it.</summary>
public class CopilotSpanCloseTests {
    const string Session = "0f1e2d3c4b5a69788796a5b4c3d2e1f0";
    const string Server  = "https://kcap.example";
    const string End     = """{"session_id":"0f1e2d3c4b5a69788796a5b4c3d2e1f0","reason":"user_exit"}""";

    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    WatcherPaths Paths => field ??= WatcherPaths.FromEnvironment(Config.Root);

    HookSpool Spool => field ??= new HookSpool(Config.Root, TimeProvider.System, Server);

    bool Owed => CopilotSpanClose.Pending(Paths, Server).Contains(Session);

    async Task<List<string>> Close(string server = Server, bool succeeds = true) {
        var asked = new List<string>();
        await CopilotSpanClose.CloseDueAsync(Paths, Spool, server, TimeProvider.System, (session, _) => {
            asked.Add(session);
            return Task.FromResult(succeeds);
        });
        return asked;
    }

    void Delivered() => CopilotSpanClose.NoteOutcome(Paths, CopilotPendingEnd.Route, End, DrainOutcome.Delivered);

    [Test]
    [Arguments(true, false)]
    [Arguments(false, true)]
    public async Task A_delivered_end_owes_its_close_until_one_succeeds(bool succeeds, bool stillOwed) {
        await Assert.That(CopilotSpanClose.Record(Paths, Session, Server, End)).IsTrue();
        Delivered();

        await Assert.That(await Close(succeeds: succeeds)).IsEquivalentTo([Session]);
        await Assert.That(Owed).IsEqualTo(stillOwed);
    }

    const string Other = "1f1e2d3c4b5a69788796a5b4c3d2e1f0";

    static string EndOf(string session) => End.Replace(Session, session);

    void Owe(string session) {
        CopilotSpanClose.Record(Paths, session, Server, EndOf(session));
        CopilotSpanClose.NoteOutcome(Paths, CopilotPendingEnd.Route, EndOf(session), DrainOutcome.Delivered);
    }

    /// <summary>A pass shares one budget, so slow closes cannot hold back the rest of a daemon tick.</summary>
    [Test]
    public async Task A_pass_starts_no_close_once_its_budget_is_spent() {
        Owe(Session);
        Owe(Other);

        var asked = new List<string>();
        await CopilotSpanClose.CloseDueAsync(Paths, Spool, Server, TimeProvider.System, async (session, _) => {
            asked.Add(session);
            await Task.Delay(300);
            return true;
        }, budget: TimeSpan.FromMilliseconds(100));

        await Assert.That(asked.Count).IsEqualTo(1);
    }

    /// <summary>A new run that drops a close while the pass is busy keeps that close from running.</summary>
    [Test]
    public async Task A_close_a_new_run_dropped_meanwhile_does_not_run() {
        Owe(Session);
        Owe(Other);

        var asked = new List<string>();
        await CopilotSpanClose.CloseDueAsync(Paths, Spool, Server, TimeProvider.System, (session, _) => {
            asked.Add(session);
            CopilotSpanClose.Discard(Paths, session == Session ? Other : Session);
            return Task.FromResult(true);
        });

        await Assert.That(asked.Count).IsEqualTo(1);
    }

    /// <summary>A marker a new end replaced while its close ran is the new end's, so it stays.</summary>
    [Test]
    public async Task A_marker_replaced_during_its_close_stays() {
        Owe(Session);

        await CopilotSpanClose.CloseDueAsync(Paths, Spool, Server, TimeProvider.System, (session, _) => {
            CopilotSpanClose.Record(Paths, session, Server, EndOf(session).Replace("user_exit", "parent_exited"));
            return Task.FromResult(true);
        });

        await Assert.That(Owed).IsTrue();
    }

    [Test]
    public async Task A_close_that_throws_stays_owed() {
        CopilotSpanClose.Record(Paths, Session, Server, End);
        Delivered();

        await CopilotSpanClose.CloseDueAsync(Paths, Spool, Server, TimeProvider.System, (_, _) => throw new IOException("locked"));

        await Assert.That(Owed).IsTrue();
    }

    /// <summary>Spans are kept per server: another server's closer finds none of this one's and would drop the debt.</summary>
    [Test]
    public async Task A_close_owed_on_one_server_is_left_to_that_server() {
        CopilotSpanClose.Record(Paths, Session, Server, End);
        Delivered();

        await Assert.That(await Close("https://other.example")).IsEmpty();
        await Assert.That(Owed).IsTrue();
    }

    [Test]
    public async Task A_close_waits_while_the_end_is_spooled() {
        CopilotSpanClose.Record(Paths, Session, Server, End);
        Spool.Append(Session, CopilotPendingEnd.Route, End);

        await Assert.That(await Close()).IsEmpty();
        await Assert.That(Owed).IsTrue();
    }

    /// <summary>An owed close recorded a moment before its end reaches the spool looks like one whose end left it, so
    /// absence alone never drops a close: a resumed run's start does, and so does age.</summary>
    [Test]
    public async Task An_undelivered_close_is_kept_until_it_is_a_day_old() {
        CopilotSpanClose.Record(Paths, Session, Server, End);

        await Assert.That(await Close()).IsEmpty();
        await Assert.That(Owed).IsTrue();

        File.SetLastWriteTimeUtc(Path.Join(Paths.Directory, Session + ".span-close-pending"), DateTime.UtcNow - TimeSpan.FromHours(25));

        await Assert.That(await Close()).IsEmpty();
        await Assert.That(Owed).IsFalse();
    }

    /// <summary>A marker a killed write left unreadable is dropped rather than skipped for good.</summary>
    [Test]
    public async Task An_unreadable_marker_is_dropped() {
        Directory.CreateDirectory(Paths.Directory);
        var marker = Path.Join(Paths.Directory, Session + ".span-close-pending");
        await File.WriteAllTextAsync(marker, """{"server":"https://kcap.exa""");

        await Assert.That(CopilotSpanClose.Pending(Paths, Server)).IsEmpty();
        await Assert.That(File.Exists(marker)).IsFalse();
    }

    [Test]
    public async Task A_rejected_end_drops_its_close() {
        CopilotSpanClose.Record(Paths, Session, Server, End);
        CopilotSpanClose.NoteOutcome(Paths, CopilotPendingEnd.Route, End, DrainOutcome.Drop);

        await Assert.That(Owed).IsFalse();
    }

    /// <summary>The delivery of another end of the session is not this one's.</summary>
    [Test]
    public async Task Only_the_owed_end_marks_its_close_delivered() {
        CopilotSpanClose.Record(Paths, Session, Server, End);
        CopilotSpanClose.NoteOutcome(Paths, CopilotPendingEnd.Route, End.Replace("user_exit", "parent_exited"), DrainOutcome.Delivered);

        await Assert.That(await Close()).IsEmpty();
    }
}
