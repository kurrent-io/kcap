using Capacitor.Cli.Core;
using Capacitor.Cli.Recording.Harness.Copilot;

namespace Capacitor.Cli.Recording.Tests.Unit.Harness.Copilot;

/// <summary>A delivered Copilot end owes its session a span close, which stays owed until a close succeeds.</summary>
public class CopilotSpanCloseTests {
    const string Session = "0f1e2d3c4b5a69788796a5b4c3d2e1f0";

    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    WatcherPaths Paths => field ??= WatcherPaths.FromEnvironment(Config.Root);

    [Test]
    [Arguments(true, false)]
    [Arguments(false, true)]
    public async Task A_close_is_owed_until_one_succeeds(bool succeeds, bool stillOwed) {
        await Assert.That(CopilotSpanClose.Record(Paths, Session)).IsTrue();

        var asked = new List<string>();
        await CopilotSpanClose.CloseDueAsync(Paths, session => {
            asked.Add(session);
            return Task.FromResult(succeeds);
        });

        await Assert.That(asked).IsEquivalentTo([Session]);
        await Assert.That(CopilotSpanClose.Pending(Paths).Contains(Session)).IsEqualTo(stillOwed);
    }

    [Test]
    public async Task A_close_that_throws_stays_owed() {
        CopilotSpanClose.Record(Paths, Session);

        await CopilotSpanClose.CloseDueAsync(Paths, _ => throw new IOException("locked"));

        await Assert.That(CopilotSpanClose.Pending(Paths).Contains(Session)).IsTrue();
    }
}
