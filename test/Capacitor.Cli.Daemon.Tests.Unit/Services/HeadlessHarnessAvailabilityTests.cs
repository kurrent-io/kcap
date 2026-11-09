using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Harness.Headless;
using Capacitor.Cli.Core.Setup;
using Capacitor.Cli.Daemon.Harness.Claude;
using Capacitor.Cli.Daemon.Harness.Codex;
using Capacitor.Cli.Daemon.Harness.OpenCode;
using Capacitor.Cli.Daemon.Harness.Pi;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>The daemon advertises a headless harness only when that harness's CLI resolves on this host.</summary>
public class HeadlessHarnessAvailabilityTests {
    [TempHome] public required TempHome Home { get; init; }
    [TempDir]  public required TempDir  Bin  { get; init; }

    HeadlessPromptRunner Runner(BinaryProbe binaries) {
        var harnesses = TestHarnesses.Under(Home, binaries);
        var config    = new DaemonConfig {
            Binaries = binaries,
            Profiles = new ProfileContext(new(null, "default", null, null), new ProfileConfig()),
        };

        var runs = new HeadlessRunRoot(Bin.PathTo("state"), "test");

        return new([
            new ClaudeHeadlessHarness(harnesses, config, TimeProvider.System, NullLogger<ClaudeHeadlessHarness>.Instance),
            new CodexHeadlessHarness(harnesses, config, TimeProvider.System, NullLogger<CodexHeadlessHarness>.Instance),
            new PiHeadlessHarness(harnesses, config, runs, TimeProvider.System, NullLogger<PiHeadlessHarness>.Instance),
            new OpenCodeHeadlessHarness(harnesses, config, runs, TimeProvider.System,
                () => new Dictionary<string, string?>(), NullLogger<OpenCodeHeadlessHarness>.Instance)
        ], TimeProvider.System, NullLogger<HeadlessPromptRunner>.Instance, CancellationToken.None);
    }

    [Test]
    public async Task A_harness_is_advertised_only_when_its_binary_resolves() {
        using var claudeOnly = Runner(TestBinaries.Searching(Bin, "claude"));
        using var none       = Runner(TestBinaries.None);

        await Assert.That(claudeOnly.AdvertisedHarnesses()).IsEquivalentTo(["claude"]);
        await Assert.That(none.AdvertisedHarnesses()).IsEmpty();
    }

    [Test]
    [Arguments(new[] { "pi" }, new[] { "pi" })]
    [Arguments(new[] { "opencode", "claude" }, new[] { "claude", "opencode" })]
    public async Task Pi_and_opencode_are_advertised_with_their_binaries(string[] binaries, string[] advertised) {
        using var runner = Runner(TestBinaries.Searching(Bin, binaries));

        await Assert.That(runner.AdvertisedHarnesses()).IsEquivalentTo(advertised);
    }
}
