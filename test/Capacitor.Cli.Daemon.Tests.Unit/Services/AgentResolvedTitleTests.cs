namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>
/// Pins the resolved-title override on the status snapshot: a title resolved after launch
/// (native extraction, generation, or the server's) replaces the prompt-derived seed in
/// <c>AgentStatusDto.Title</c>, and applying one pulses the status so clients repaint.
/// </summary>
public class AgentResolvedTitleTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }
    [TempHome] public required TempHome Home { get; init; }

    OrchestratorFixture Build() => new(Config, Home, "resolved-title-test", notifyOrchestrator: true);

    [Test]
    public async Task Resolved_title_replaces_the_prompt_seed_in_the_snapshot() {
        var fx = Build();
        try {
            var agent = fx.Orchestrator.SeedAgentForTest("t-1", prompt: "Fix the flaky test\nmore context");

            fx.Orchestrator.SetResolvedTitle(agent, "Stabilize the daemon lease test");

            var dto = fx.Orchestrator.SnapshotAgentsForStatus().Single();
            await Assert.That(dto.Title).IsEqualTo("Stabilize the daemon lease test");
        } finally { await fx.DisposeAsync(); }
    }

    [Test]
    public async Task Without_a_resolved_title_the_prompt_seed_stands() {
        var fx = Build();
        try {
            fx.Orchestrator.SeedAgentForTest("t-1", prompt: "Fix the flaky test");

            var dto = fx.Orchestrator.SnapshotAgentsForStatus().Single();
            await Assert.That(dto.Title).IsEqualTo("Fix the flaky test");
        } finally { await fx.DisposeAsync(); }
    }

    [Test]
    public async Task Applying_a_resolved_title_pulses_the_status_once() {
        var fx = Build();
        try {
            var agent = fx.Orchestrator.SeedAgentForTest("t-1", prompt: "Fix the flaky test");

            var v0 = fx.Notifier.Version;
            fx.Orchestrator.SetResolvedTitle(agent, "Stabilize the daemon lease test");
            await Assert.That(fx.Notifier.Version).IsGreaterThan(v0);

            // Re-applying the same title is a no-op — no spurious repaint.
            var v1 = fx.Notifier.Version;
            fx.Orchestrator.SetResolvedTitle(agent, "Stabilize the daemon lease test");
            await Assert.That(fx.Notifier.Version).IsEqualTo(v1);
        } finally { await fx.DisposeAsync(); }
    }
}
