using Capacitor.Cli.Daemon.Services;
using Capacitor.Cli.Daemon.Tests.Unit.Pty;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>
/// Graceful-exit timeout diagnostics identify the vendor that failed to exit.
/// </summary>
public class AgentOrchestratorStopDiagnosticsTests {
    [TempDir] public required TempDir Worktree { get; init; }

    /// <summary>A directory under the fixture, never the fixture itself: cleanup of a standalone
    /// worktree deletes the path it is given, and a fixture that vanishes under its own test takes
    /// the reason for every later failure with it.</summary>
    string WorktreePath => Worktree.CreateDir("worktree");

    sealed class CapturingOrchestratorLogger : ILogger<AgentOrchestrator> {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception));
    }

    [Test]
    [Arguments("cursor")]
    [Arguments("codex")]
    [Arguments("copilot")]
    public async Task Graceful_exit_timeout_warning_names_the_agents_own_vendor(string vendor) {
        var log = new CapturingOrchestratorLogger();
        await using var orch = AgentOrchestratorHarness.BuildOrchestrator(
            new CaptureServerConnection(), new SpyPtyProcessFactory(),
            new Dictionary<string, IHostedAgentLauncher>(), logger: log);

        // FakeHostedAgentRuntime reports HasExited == false until TerminateAsync runs and returns
        // from WaitForExitAsync immediately — i.e. exactly the "graceful window elapsed without the
        // CLI exiting" state, with no real 15s wait.
        orch.RegisterAgentForTest(new AgentInstance(
            $"agent-{vendor}", null, "", null, WorktreePath, vendor,
            new FakeHostedAgentRuntime(vendor, emitsTerminalOutput: false),
            new WorktreeInfo(WorktreePath, "", WorktreePath, IsStandalone: true), new CancellationTokenSource()) {
            ActivityClock = new AgentActivityClock(TimeProvider.System),
            CreatedAt     = DateTime.UtcNow,
            LastOutputAt  = DateTime.UtcNow
        });

        await orch.HandleStopAgent($"agent-{vendor}");

        var warning = log.Messages.SingleOrDefault(m => m.Contains("Graceful /exit window", StringComparison.Ordinal));
        await Assert.That(warning).IsNotNull();
        await Assert.That(warning!).Contains($"without {vendor} exiting");
        // The whole point: no other vendor's name may appear in this line.
        await Assert.That(warning).DoesNotContain("claude");
    }

    [Test]
    public async Task Graceful_exit_timeout_warning_still_names_claude_for_a_claude_agent() {
        var log = new CapturingOrchestratorLogger();
        await using var orch = AgentOrchestratorHarness.BuildOrchestrator(
            new CaptureServerConnection(), new SpyPtyProcessFactory(),
            new Dictionary<string, IHostedAgentLauncher>(), logger: log);

        orch.RegisterAgentForTest(new AgentInstance(
            "agent-claude", null, "", null, WorktreePath, "claude",
            new FakeHostedAgentRuntime("claude", emitsTerminalOutput: false),
            new WorktreeInfo(WorktreePath, "", WorktreePath, IsStandalone: true), new CancellationTokenSource()) {
            ActivityClock = new AgentActivityClock(TimeProvider.System),
            CreatedAt     = DateTime.UtcNow,
            LastOutputAt  = DateTime.UtcNow
        });

        await orch.HandleStopAgent("agent-claude");

        var warning = log.Messages.SingleOrDefault(m => m.Contains("Graceful /exit window", StringComparison.Ordinal));
        await Assert.That(warning).IsNotNull();
        await Assert.That(warning!).Contains("without claude exiting");
    }

    /// <summary>A terminal-dimensions send the shutdown cancels is expected and logged without a stack; the
    /// same cancellation while the daemon runs on, or any other failure, stays a warning.</summary>
    [Test]
    [Arguments(true, true, true)]
    [Arguments(true, false, false)]
    [Arguments(false, true, false)]
    public async Task Only_a_cancellation_during_shutdown_is_a_quiet_dims_failure(bool cancelled, bool shuttingDown, bool quiet) {
        Exception failure = cancelled ? new TaskCanceledException() : new IOException("connection reset");

        await Assert.That(AgentOrchestrator.IsShutdownCancellation(failure, shuttingDown)).IsEqualTo(quiet);
    }
}
