using System.Net.Sockets;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Core.Resume;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Hosting;
using TUnit.Core.Enums;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>Drives the AgentResume frame through a real <see cref="LocalControlServer"/> socket.</summary>
[ExcludeOn(OS.Windows)] // Unix-domain socket path
public class LocalControlAgentResumeTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }
    [TempDir] public required TempDir Tmp { get; init; }
    [TempHome] public required TempHome Home { get; init; }

    sealed class CountingLifetime : IHostApplicationLifetime {
        int StopCalls;
        public CancellationToken ApplicationStarted  => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped  => CancellationToken.None;
        public void StopApplication() => Interlocked.Increment(ref StopCalls);
    }

    sealed class SpyStrategy : IRestartStrategy {
        public int            Calls;
        public RestartOutcome Restart() { Calls++; return RestartOutcome.Initiated; }
    }

    async Task RunAsync(string name, Func<AgentOrchestrator, Func<LocalFrame, Task<LocalFrame?>>, Task> body) {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        await using var fx = new OrchestratorFixture(Config, Home, name, lifetime: new CountingLifetime());
        await fx.StartServerAsync(cts.Token, new SpyStrategy());

        async Task<LocalFrame?> Send(LocalFrame frame) {
            using var sock = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await sock.ConnectAsync(new UnixDomainSocketEndPoint(fx.SockPath), cts.Token);
            await using var stream = new NetworkStream(sock, ownsSocket: false);
            await FrameCodec.WriteAsync(stream, frame, cts.Token);
            return await FrameCodec.ReadAsync(stream, cts.Token);
        }

        await body(fx.Orchestrator, Send);
    }

    ResumeLedgerEntry Entry(AgentOrchestrator orch, string agentId, ResumeState state, int attempts = 2) {
        var stamp  = TimeProvider.System.GetUtcNow();
        var launch = new ResumeLaunchDescriptor(
            Prompt: "build the thing", Model: "auto", Effort: null, RepoPath: Tmp.PathTo("repo"),
            WorktreePath: Tmp.CreateDir($"wt-{agentId}"), Work: WorkLocation.OwnedWorktree,
            GitRelativeCwd: null, SandboxPolicy: null, ApprovalPolicy: null, PermissionPreset: null,
            McpConfigPath: null, Title: "the thing", IsPrivate: false, Origin: "server",
            RequesterUserId: "user-1", RequesterDisplay: "User One", ConsentSource: "default",
            ConsentAt: stamp.ToString("O"), FlowRunId: null, FlowRole: null,
            WorktreeBranch: null, WorktreeIsStandalone: false, WorktreeSourceRepo: null);
        var entry = new ResumeLedgerEntry(
            ResumeLedgerEntry.CurrentSchemaVersion, agentId, "daemon-1", stamp, stamp, "cursor", "acp",
            $"session-{agentId}", "vendor-session-1", launch, state, attempts, LastError: "runtime_exited",
            PredecessorInstanceId: null, ResumingAt: null, DaemonInstanceId: null);
        orch.ResumeLedger!.Write(entry);

        return entry;
    }

    [Test]
    public async Task AgentResume_on_an_interrupted_entry_acks_resuming_and_schedules_the_lane() =>
        await RunAsync("ar-int", async (orch, send) => {
            Entry(orch, "agent-1", ResumeState.Interrupted);
            var continued = new TaskCompletionSource<ResumeLedgerEntry>(TaskCreationOptions.RunContinuationsAsynchronously);
            orch.ContinueEntry = (entry, _, _) => {
                continued.TrySetResult(entry);

                return Task.FromResult(ResumeOutcome.Continued);
            };

            var reply = await send(LocalFrame.AgentResume("agent-1"));

            await Assert.That(reply!.Type).IsEqualTo(FrameType.AgentResumeAck);
            await Assert.That(reply.Text).IsEqualTo("resuming");

            var ran = await continued.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Assert.That(ran.AgentId).IsEqualTo("agent-1");
            await Assert.That(ran.State).IsEqualTo(ResumeState.Resuming);
            await Assert.That(ran.Attempts).IsEqualTo(2);
            await Assert.That(ran.LastError).IsNull();
            await Assert.That(ran.ResumingAt).IsNotNull();
            await Assert.That(orch.ResumeLedger!.TryRead("agent-1")!.State).IsEqualTo(ResumeState.Resuming);
        });

    [Test]
    public async Task AgentResume_on_a_running_entry_acks_not_interrupted() =>
        await RunAsync("ar-run", async (orch, send) => {
            Entry(orch, "agent-2", ResumeState.Running);

            var reply = await send(LocalFrame.AgentResume("agent-2"));

            await Assert.That(reply!.Type).IsEqualTo(FrameType.AgentResumeAck);
            await Assert.That(reply.Text).IsEqualTo("not_interrupted");
            await Assert.That(orch.ResumeLedger!.TryRead("agent-2")!.State).IsEqualTo(ResumeState.Running);
        });

    [Test]
    public async Task AgentResume_on_an_unknown_id_acks_unknown() =>
        await RunAsync("ar-unk", async (_, send) => {
            var reply = await send(LocalFrame.AgentResume("nobody"));

            await Assert.That(reply!.Type).IsEqualTo(FrameType.AgentResumeAck);
            await Assert.That(reply.Text).IsEqualTo("unknown");
        });

    [Test]
    [ExcludeOn(OS.Windows), System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public async Task AgentResume_with_an_unwritable_ledger_answers_an_error_and_leaves_the_entry_interrupted() =>
        await RunAsync("ar-ro", async (orch, send) => {
            var entry = Entry(orch, "agent-3", ResumeState.Interrupted);
            var dir   = Path.GetDirectoryName(orch.ResumeLedger!.RingPath("agent-3"))!;
            var mode  = File.GetUnixFileMode(dir);
            File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserExecute);

            try {
                var reply = await send(LocalFrame.AgentResume("agent-3"));

                await Assert.That(reply!.Type).IsEqualTo(FrameType.Error);
            } finally {
                File.SetUnixFileMode(dir, mode);
            }

            var stored = orch.ResumeLedger!.TryRead("agent-3")!;
            await Assert.That(stored.State).IsEqualTo(ResumeState.Interrupted);
            await Assert.That(stored.Attempts).IsEqualTo(entry.Attempts);
        });
}
