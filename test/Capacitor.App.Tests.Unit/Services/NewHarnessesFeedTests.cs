using Capacitor.App.Services;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Harness.Copilot;
using Capacitor.Cli.Core.Setup;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.App.Tests.Unit.Services;

public class NewHarnessesFeedTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    /// <summary>VS Code's first launch is enough for Copilot to be named with VS Code, before any CLI or chat, and
    /// once the offer is recorded the tool is no longer new.</summary>
    [Test]
    public async Task Vs_code_copilot_found_after_setup_is_named_until_it_is_offered() {
        using var tmp = new TempDir();
        tmp.CreateDir("copilot", "ide");
        var harnesses = HarnessRegistry.Over(
            BinaryProbe.Searching(null), CopilotHarness.Over(new CopilotPaths(new UserHome(tmp.Path), null, tmp.PathTo("copilot"))));
        var time   = new FakeTimeProvider(new DateTimeOffset(2026, 11, 16, 12, 0, 0, TimeSpan.Zero));
        var offers = new HarnessOfferStore(Config.Root, time);

        var found = NewHarnessesFeed.Read(harnesses, offers, time);

        await Assert.That(found!.Labels).IsEquivalentTo(["VS Code Copilot"]);
        await found.Offered();
        await Assert.That(offers.Load().Entry(HarnessId.Copilot)?.LastOffered).IsEqualTo(time.GetUtcNow());
        await Assert.That(NewHarnessesFeed.Read(harnesses, offers, time)).IsNull();
    }
}
