using System.Security.Cryptography;
using System.Text;
using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Services;
using Capacitor.Cli.Daemon.Tests.Unit.Pty;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

public class AgentWorkspaceNamesTests {
    const string ServerMintedId = "0123456789abcdef0123456789abcdef";

    static string Sha256Prefix(string agentId) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(agentId))).ToLowerInvariant()[..16];

    /// <summary>The names carry 16 hex of the id's SHA-256 whatever the id's length or content, so a
    /// 32-hex server id and an id holding separators both yield the same short, path-safe name.</summary>
    [Test]
    [Arguments(ServerMintedId)]
    [Arguments("a1b2c3d4")]
    [Arguments("../x/y")]
    public async Task Names_are_a_fixed_prefix_plus_sixteen_hex_of_the_ids_sha256(string agentId) {
        await Assert.That(AgentWorkspaceNames.BorrowedSnapshot(agentId)).IsEqualTo("borrowed-" + Sha256Prefix(agentId));
        await Assert.That(AgentWorkspaceNames.Scratch(agentId)).IsEqualTo("scratch-" + Sha256Prefix(agentId));
    }

    [Test]
    public async Task A_scratch_launch_runs_in_the_short_name() {
        var factory = new CapturingFactory();
        await using var orch = AgentOrchestratorHarness.BuildOrchestrator(new CaptureServerConnection(), new SpyPtyProcessFactory(),
            new Dictionary<string, IHostedAgentLauncher>(), extraRuntimeFactories: [factory]);

        await orch.HandleLaunchAgentForTest(new LaunchAgentCommand(
            ServerMintedId, "review this", "auto", null, "", null, null, factory.Vendor, LaunchKind.ReviewFlow, ScratchWorkspace: true));

        var path = factory.LastContext!.Worktree.Path;
        await Assert.That(Path.GetFileName(path)).IsEqualTo("scratch-" + Sha256Prefix(ServerMintedId));
        await Assert.That(Path.GetFileName(Path.GetDirectoryName(path))).IsEqualTo(WorktreeManager.ScratchDirectoryName);
    }

    /// <summary>Directories an older daemon named with the raw agent id are reaped beside the short-named
    /// ones, while a live workspace under either name survives.</summary>
    [Test]
    public async Task The_orphan_sweep_reaps_raw_id_and_short_named_workspaces_alike() {
        using var tmp = new TempDir();
        var root = tmp.PathTo("root");
        string Dir(string parent, string name) {
            var path = Path.Combine(root, parent, name);
            Directory.CreateDirectory(path);
            return path;
        }
        const string snapshots = WorktreeManager.BorrowedSnapshotsDirectoryName;
        const string scratch   = WorktreeManager.ScratchDirectoryName;
        var deadOldSnapshot  = Dir(snapshots, $"borrowed-{ServerMintedId}");
        var deadOldScratch   = Dir(scratch, $"scratch-{ServerMintedId}");
        var deadNewSnapshot  = Dir(snapshots, AgentWorkspaceNames.BorrowedSnapshot(ServerMintedId));
        var deadNewScratch   = Dir(scratch, AgentWorkspaceNames.Scratch(ServerMintedId));
        var liveOldSnapshot  = Dir(snapshots, "borrowed-live-agent");
        var liveNewScratch   = Dir(scratch, AgentWorkspaceNames.Scratch("live-agent"));
        var manager = new WorktreeManager(
            new DaemonConfig { WorktreeRoot = root }, NullLogger<WorktreeManager>.Instance, NoSnapshotBarrier.Instance, TimeProvider.System);

        await manager.CleanupOrphanedAsync([liveOldSnapshot, liveNewScratch]);

        foreach (var dead in new[] { deadOldSnapshot, deadOldScratch, deadNewSnapshot, deadNewScratch })
            await Assert.That(Directory.Exists(dead)).IsFalse();
        await Assert.That(Directory.Exists(liveOldSnapshot)).IsTrue();
        await Assert.That(Directory.Exists(liveNewScratch)).IsTrue();
    }

    sealed class CapturingFactory : IHostedAgentRuntimeFactory {
        readonly SpyAcpHostedAgentRuntimeFactory _inner = new("cursor");
        public string CliPath => _inner.CliPath;
        public string Vendor => _inner.Vendor;
        public bool SupportsUnattended => true;
        public RuntimeStartContext? LastContext => _inner.LastContext;
        public bool IsAvailable() => true;
        public Task<HostedRuntimeStart> StartAsync(RuntimeStartContext ctx, CancellationToken ct) => _inner.StartAsync(ctx, ct);
    }
}
