using Capacitor.Cli.Core.Setup;
using Capacitor.Cli.Daemon.Services;
using Capacitor.Cli.Daemon.Tests.Unit.Pty;
using TUnit.Core.Enums;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>Which vendor CLI titles a hosted agent. Fake <c>claude</c> and <c>pi</c> scripts each answer with their
/// own title, so the title returned names the runner that produced it.</summary>
public class AgentOrchestratorTitleGenerationTests {
    [TempDir] public required TempDir Tmp { get; init; }

    const string ClaudeReply = """{"result":"Claude title"}""";
    const string PiReply     = """{"type":"agent_end","messages":[{"role":"assistant","content":[{"type":"text","text":"Pi title"}],"stopReason":"stop"}]}""";

    async Task<string?> Generate(string vendor) {
        var bin = Tmp.CreateDir("bin");
        bin.CreateExecutable("claude", $"#!/bin/sh\necho '{ClaudeReply}'\n");
        bin.CreateExecutable("pi", $"#!/bin/sh\ncat > /dev/null\necho '{PiReply}'\n");

        await using var orch = AgentOrchestratorHarness.BuildOrchestrator(
            new CaptureServerConnection(), new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>(),
            harnessBinaries: BinaryProbe.Searching(bin.Path));

        return await orch.GenerateTitleForAsync(
            new TitleAgentView("a1", vendor, "Fix the login timeout", null, null, DateTime.UtcNow), CancellationToken.None);
    }

    [Test, ExcludeOn(OS.Windows)]
    [Arguments("pi", "Pi title")]
    [Arguments("claude", "Claude title")]
    [Arguments("opencode", "Claude title")]
    public async Task A_hosted_agent_is_titled_by_its_vendors_runner(string vendor, string expected) =>
        await Assert.That(await Generate(vendor)).IsEqualTo(expected);
}
