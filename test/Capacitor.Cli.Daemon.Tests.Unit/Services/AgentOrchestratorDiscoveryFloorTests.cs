using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Harness.Antigravity;
using Capacitor.Cli.Daemon.Services;
using Capacitor.Cli.Daemon.Tests.Unit.Pty;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Core.Enums;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

[RunOn(OS.MacOs | OS.Linux), ParallelLimiter<SubprocessLimit>]
public class AgentOrchestratorDiscoveryFloorTests {
    [TempDir] public required TempDir Tmp { get; init; }

    [Test]
    [Arguments(null, true)]
    [Arguments("2.0.0", false)]
    [Arguments("", false)]
    public async Task Discovery_seeds_only_an_absent_floor_before_classifying_a_later_install(string? existingFloor, bool advertised) {
        var config = new DaemonConfig { Binaries = TestBinaries.None, AntigravityPath = Tmp.PathTo("agy") };
        var factory = new AntigravityHostedAgentRuntimeFactory(config, NullLoggerFactory.Instance, TimeProvider.System);
        await using var orch = AgentOrchestratorHarness.BuildOrchestrator(
            new CaptureServerConnection(), new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>(),
            extraRuntimeFactories: [factory], sharedConfig: config, configure: c => {
                c.SupportedVendors = [];
                c.UnattendedVendors = [];
                c.UnattendedVendorCapabilities = [];
            });
        var stateDir = config.Store.StateDirectory(config.Name);
        DaemonRunner.SeedReviewerFloors(stateDir, config); // boot: no binary is installed yet
        var store = AntigravityHostedAgentRuntimeFactory.VersionStoreFor(config);
        var recordPath = Path.Combine(stateDir, ReviewerVersionStore.FileNameFor("antigravity"));
        if (existingFloor is { Length: > 0 }) store.Affirm(existingFloor);
        else if (existingFloor is not null) await File.WriteAllTextAsync(recordPath, "corrupt record");
        var originalRecord = File.Exists(recordPath) ? await File.ReadAllTextAsync(recordPath) : null;

        Tmp.CreateExecutable("agy", "#!/bin/sh\nprintf '1.1.10\\n'\n");
        orch.RefreshAgentDiscovery();
        await orch.CapabilityRefreshForTest;

        await Assert.That(config.SupportedVendors).IsEquivalentTo(["antigravity"]);
        await Assert.That(config.UnattendedVendors!.Contains("antigravity")).IsEqualTo(advertised);
        if (originalRecord is not null)
            await Assert.That(await File.ReadAllTextAsync(recordPath)).IsEqualTo(originalRecord);
        else
            await Assert.That(store.Affirmed).IsEqualTo("1.1.10");
    }
}
