using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using DynamicData;
using Microsoft.Extensions.Time.Testing;
using static Capacitor.App.Tests.Unit.AvaloniaSession;
using static Capacitor.App.Tests.Unit.WorkspaceFixtures;

namespace Capacitor.App.Tests.Unit.ViewModels;

[NotInParallel(nameof(AvaloniaSession))]
public class SessionRunMonitorTests {
    [TempDir] public required TempDir Tmp { get; init; }

    const string AgentCallLine = """{"type":"assistant","message":{"content":[{"type":"tool_use","id":"toolu_A","name":"Agent","input":{"description":"Map","prompt":"go","subagent_type":"Explore"}}]}}""";

    sealed class Rig : IDisposable {
        public FakeDaemonClientService Daemon { get; } = new();
        FakeAgentDirectory Directory { get; } = new();
        FakeTimeProvider Time { get; } = new();
        FakeServerLane Lane { get; } = new();
        SessionAccessService Access { get; }
        public RunningWork Latest;
        public bool Ended;

        public Rig() => Access = new SessionAccessService(Lane, Time);

        public SessionRunMonitor Monitor() => new("local:a1", work => Latest = work, () => Ended = true, Daemon, Directory, Access,
            FakeSessionEvents.Paged(_ => Task.FromResult(new SessionDetailFetch(null))), Lane, Time);

        public void Dispose() {
            Access.Dispose();
            Directory.Dispose();
        }
    }

    /// A live local session's transcript is tailed on the poll; one already ended never starts it.
    [Test]
    public async Task A_local_monitor_polls_only_while_tailing_a_live_session() {
        await RunOnUiAsync(async () => {
            using var rig = new Rig();
            var path = Tmp.CreateFile("t.jsonl", [AgentCallLine]);
            rig.Daemon.Agents.AddOrUpdate(Agent("a1", "claude", hasTerminal: true) with { TranscriptPath = path });
            using (var live = rig.Monitor()) {
                await WaitUntilAsync(() => rig.Latest.Subagents == 1, what: "the tailed subagent");
                await Assert.That(live.IsTickingForTesting).IsTrue();
            }

            rig.Daemon.Agents.AddOrUpdate(Agent("a1", "claude", hasTerminal: true) with { TranscriptPath = path, Status = "Completed" });
            using var ended = rig.Monitor();
            await WaitUntilAsync(() => rig.Ended, what: "the ended verdict");
            await Assert.That(ended.IsTickingForTesting).IsFalse();
        });
    }
}
