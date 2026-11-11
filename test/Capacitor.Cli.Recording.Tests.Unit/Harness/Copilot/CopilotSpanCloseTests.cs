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
        await CopilotSpanClose.CloseDueAsync(Paths, Spool, server, session => {
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

    [Test]
    public async Task A_close_that_throws_stays_owed() {
        CopilotSpanClose.Record(Paths, Session, Server, End);
        Delivered();

        await CopilotSpanClose.CloseDueAsync(Paths, Spool, Server, _ => throw new IOException("locked"));

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

    /// <summary>An end gone from the spool undelivered was superseded by a resumed run, or never spooled: closing then
    /// would close the resumed run's span.</summary>
    [Test]
    public async Task An_end_that_left_the_spool_undelivered_drops_its_close() {
        CopilotSpanClose.Record(Paths, Session, Server, End);

        await Assert.That(await Close()).IsEmpty();
        await Assert.That(Owed).IsFalse();
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
