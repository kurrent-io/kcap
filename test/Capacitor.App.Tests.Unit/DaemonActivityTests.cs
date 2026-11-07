using Capacitor.App.Services;
using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.App.Tests.Unit;

public class DaemonActivityTests {
    static AgentStatusDto Agent(
            string status, bool? awaitingInput = true, int? liveSubagents = null, TerminalDialogDto? dialog = null) =>
        new("a1", "agent", "claude", "/repo", status, null, null, null, DateTime.UtcNow, null, null,
            AwaitingInput: awaitingInput, LiveSubagents: liveSubagents, TerminalDialog: dialog);

    static DaemonStatusDto Snapshot(List<PendingLaunchDto>? pending, params AgentStatusDto[] agents) =>
        new(new DaemonInfoDto("daemon-a", "1.4.0", "https://cap.example.test", "connected", 4, agents.Length), [.. agents], pending, Busy: agents.Length > 0);

    static readonly PendingLaunchDto Launch = new("p1", "claude", "/repo", null, DateTime.UtcNow, "starting");

    [Test]
    public async Task An_agent_waiting_on_its_user_is_quiet() =>
        await Assert.That(DaemonActivity.QuietIn(Snapshot([], Agent("Running"), Agent("Completed", awaitingInput: false)))).IsTrue();

    [Test]
    public async Task No_agents_and_no_launches_is_quiet() =>
        await Assert.That(DaemonActivity.QuietIn(Snapshot([]))).IsTrue();

    /// <summary>A turn the daemon does not report as ended, background work, a question on screen, an agent coming up,
    /// a status this app does not know, or a launch list left out all count as work.</summary>
    [Test]
    [MethodDataSource(nameof(NotQuiet))]
    public async Task Work_or_an_unreported_turn_is_not_quiet(string name, DaemonStatusDto snapshot) =>
        await Assert.That(DaemonActivity.QuietIn(snapshot)).IsFalse().Because(name);

    public static IEnumerable<Func<(string, DaemonStatusDto)>> NotQuiet() {
        yield return () => ("mid-turn", Snapshot([], Agent("Running", awaitingInput: false)));
        yield return () => ("turn unreported", Snapshot([], Agent("Running", awaitingInput: null)));
        yield return () => ("subagent running", Snapshot([], Agent("Running", liveSubagents: 1)));
        yield return () => ("question on screen", Snapshot([], Agent("Running", dialog: new TerminalDialogDto("Trust this folder?", "", ["Yes", "No"], 0, ""))));
        yield return () => ("starting", Snapshot([], Agent("Starting")));
        yield return () => ("resuming", Snapshot([], Agent("Resuming")));
        yield return () => ("unknown status", Snapshot([], Agent("Paused")));
        yield return () => ("launch starting", Snapshot([Launch]));
        yield return () => ("launches unreported", Snapshot(null));
    }
}
