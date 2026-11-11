using Capacitor.Cli.Core;
using Capacitor.Cli.Recording.Harness.Copilot;

namespace Capacitor.Cli.Recording.Tests.Unit.Harness.Copilot;

/// <summary>A spooled Copilot end owes its session a span close on its server, paid once that end has left the spool and
/// owed until a close succeeds.</summary>
public class CopilotSpanCloseTests {
    const string Session = "0f1e2d3c4b5a69788796a5b4c3d2e1f0";
    const string Server  = "https://kcap.example";

    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    WatcherPaths Paths => field ??= WatcherPaths.FromEnvironment(Config.Root);

    HookSpool Spool => field ??= new HookSpool(Config.Root, TimeProvider.System, Server);

    async Task<List<string>> Close(string server, bool succeeds = true) {
        var asked = new List<string>();
        await CopilotSpanClose.CloseDueAsync(Paths, Spool, server, session => {
            asked.Add(session);
            return Task.FromResult(succeeds);
        });
        return asked;
    }

    [Test]
    [Arguments(true, false)]
    [Arguments(false, true)]
    public async Task A_close_is_owed_until_one_succeeds(bool succeeds, bool stillOwed) {
        await Assert.That(CopilotSpanClose.Record(Paths, Session, Server)).IsTrue();

        await Assert.That(await Close(Server, succeeds)).IsEquivalentTo([Session]);
        await Assert.That(CopilotSpanClose.Pending(Paths, Server).Contains(Session)).IsEqualTo(stillOwed);
    }

    [Test]
    public async Task A_close_that_throws_stays_owed() {
        CopilotSpanClose.Record(Paths, Session, Server);

        await CopilotSpanClose.CloseDueAsync(Paths, Spool, Server, _ => throw new IOException("locked"));

        await Assert.That(CopilotSpanClose.Pending(Paths, Server).Contains(Session)).IsTrue();
    }

    /// <summary>Spans are kept per server: another server's closer finds none of this one's and would drop the debt.</summary>
    [Test]
    public async Task A_close_owed_on_one_server_is_left_to_that_server() {
        CopilotSpanClose.Record(Paths, Session, Server);

        await Assert.That(await Close("https://other.example")).IsEmpty();
        await Assert.That(CopilotSpanClose.Pending(Paths, Server).Contains(Session)).IsTrue();
    }

    /// <summary>The span follows the end, so a close waits while the end is still spooled.</summary>
    [Test]
    public async Task A_close_waits_for_the_end_to_leave_the_spool() {
        CopilotSpanClose.Record(Paths, Session, Server);
        Spool.Append(Session, CopilotPendingEnd.Route, "{}");

        await Assert.That(await Close(Server)).IsEmpty();
        await Assert.That(CopilotSpanClose.Pending(Paths, Server).Contains(Session)).IsTrue();
    }
}
