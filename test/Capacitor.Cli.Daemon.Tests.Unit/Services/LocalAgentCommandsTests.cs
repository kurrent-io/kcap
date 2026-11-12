using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Daemon.Services;
using Capacitor.Cli.Daemon.Tests.Unit.Pty;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>The AgentCommands frame answers the slash commands the agent's harness last reported.</summary>
public class LocalAgentCommandsTests {
    static AgentOrchestrator Build() =>
        AgentOrchestratorHarness.BuildOrchestrator(
            new CaptureServerConnection(), new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>(),
            timeProvider: new FakeTimeProvider());

    static async Task<HostedAgentCommand[]> Ask(AgentOrchestrator orch, string agentId) {
        using var ms = new MemoryStream();
        await orch.HandleLocalAgentCommandsAsync(agentId, ms, CancellationToken.None);
        ms.Position = 0;
        var frame = await FrameCodec.ReadAsync(ms, CancellationToken.None);
        await Assert.That(frame!.Type).IsEqualTo(FrameType.AgentCommandsReply);

        return JsonSerializer.Deserialize(frame.Text, AgentCommandsIpcJsonContext.Default.HostedAgentCommandArray)!;
    }

    [Test]
    public async Task An_agent_answers_its_reported_commands() {
        await using var orch = Build();
        var agent = AgentOrchestratorHarness.SeedAcpAgent(orch, "live", new FakeAcpRuntime());
        orch.SetCommands(agent, [new HostedAgentCommand("review", "Review the diff", "[pr]")]);

        await Assert.That(await Ask(orch, "live")).IsEquivalentTo([new HostedAgentCommand("review", "Review the diff", "[pr]")]);
    }

    [Test]
    public async Task An_unknown_agent_answers_none() {
        await using var orch = Build();

        await Assert.That(await Ask(orch, "nobody")).IsEmpty();
    }
}
