using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Services;
using Capacitor.Cli.Daemon.Tests.Unit.Pty;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

[ParallelLimiter<SubprocessLimit>]
public class AgentOrchestratorRepoPathTests {
    [TempDir] public required TempDir Tmp { get; init; }

    /// <summary>The path sent names a link; what the allow-list judged is its target, so the worktree and
    /// the runtime must be given the target, never the link that could be repointed after the check.</summary>
    [Test]
    public async Task A_launch_runs_from_the_directory_its_allow_check_resolved_and_reports_the_path_sent() {
        using var repo = GitRepo.CreateWithCommit();
        var canonical  = CanonicalPath.Resolve(repo);
        var link       = Tmp.PathTo("link");
        Directory.CreateSymbolicLink(link, canonical);

        var server = new CaptureServerConnection();
        var claude = new SpyHostedAgentRuntimeFactory("claude") { SupportsUnattended = true };

        await using var orch = AgentOrchestratorHarness.BuildOrchestrator(
            server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>(),
            allowedRepoPath: canonical, extraRuntimeFactories: [claude]);

        await orch.HandleLaunchAgentForTest(new LaunchAgentCommand(
            AgentId: "agent-link", Prompt: "do work", Model: "default", Effort: null, RepoPath: link,
            Tools: null, AttachmentIds: null, Vendor: "claude"));

        await Assert.That(link).IsNotEqualTo(canonical);
        await Assert.That(claude.LastContext).IsNotNull();
        await Assert.That(claude.LastContext!.SourceRepoPath).IsEqualTo(canonical);
        await Assert.That(claude.LastContext.Worktree.SourceRepo).IsEqualTo(canonical);
        await Assert.That(server.AgentRegisteredRepoPaths).Contains(("agent-link", (string?)link));
    }
}
