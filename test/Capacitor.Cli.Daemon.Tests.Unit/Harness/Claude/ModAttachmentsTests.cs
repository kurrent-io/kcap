using System.Diagnostics;
using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Daemon.Harness.Claude;
using Capacitor.Cli.Daemon.Services;
using Capacitor.Cli.Daemon.Tests.Unit.Pty;
using Capacitor.Cli.Daemon.Tests.Unit.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using TUnit.Core.Enums;

namespace Capacitor.Cli.Daemon.Tests.Unit.Harness.Claude;

/// <summary>The terminal sessions the daemon steers: what it holds for each, when it asks the server, what it reports and
/// under which agent, and that nothing about an attached agent reaches its process or holds up the daemon.</summary>
public class ModAttachmentsTests {
    [TempDaemonPaths] public required TempDaemonStore Daemons { get; init; }

    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    const string Session  = "0f8fad5b-d9cb-469f-a165-70867728950e";
    const string Later    = "7c9e6679-7425-40de-944b-e07fc1f90ae7";
    const string Key      = "4242:Sat Nov  8 10:00:00 2026";
    const string OtherKey = "4343:Sat Nov  8 10:05:00 2026";

    readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 8, 10, 0, 0, TimeSpan.Zero));

    string StatePath => Daemons.Store.SteeringAttachmentsPath("test");

    static string AgentOf(string session) => ModAttachments.AgentIdFor(SessionIds.Canonical(session)!);

    ModAttachments Attachments(ServerConnection server, bool offered = true, Func<bool>? steeringOn = null) =>
        new(server, new ModAttachmentStore(StatePath, NullLogger.Instance), steeringOn ?? (static () => true), _time, NullLogger.Instance,
            offered: offered);

    static AgentOrchestrator Orchestrator(CaptureServerConnection server, ModAttachments attachments) =>
        AgentOrchestratorHarness.BuildOrchestrator(
            server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>(), attachments: _ => attachments);

    static async Task<string> AttachAsync(ModAttachments attachments, string session = Session, string key = Key, string? replaces = null) {
        var reply = await attachments.AttachAsync(session, key, replaces, CancellationToken.None);

        return reply.AgentId ?? throw new InvalidOperationException($"refused: {reply.Refused}");
    }

    static void Push(ModAttachments attachments, string agentId, Guid dispatchId, string text = "hello") =>
        attachments.TakeInput(new SendInputCommand(agentId, text, null, dispatchId));

    static Task<SteeringNextReply?> IdleAsync(ModAttachments attachments, string agentId, string key = Key) =>
        attachments.NextAsync(agentId, key, idle: true, outstanding: null);

    sealed class SpyStrategy : IRestartStrategy {
        public int Calls;

        public RestartOutcome Restart() {
            Calls++;

            return RestartOutcome.Initiated;
        }
    }

    /// <summary>The attached Claude is someone's terminal session: a stop from the server, from <c>kcap agent stop</c>
    /// and from the runtime's own stop calls ends the attachment and leaves the process running.</summary>
    [Test]
    [ExcludeOn(OS.Windows)] // spawns sleep
    public async Task Stop_touches_nothing_in_the_process() {
        using var child = Process.Start(new ProcessStartInfo("sleep", "30") { UseShellExecute = false })!;

        try {
            var server = new CaptureServerConnection();
            await using var attachments = Attachments(server);
            await using var orch = Orchestrator(server, attachments);

            var fromServer = await AttachAsync(attachments, key: $"{child.Id}:start");
            var fromCli    = await AttachAsync(attachments, session: Later, key: $"{child.Id}:start2");
            var runtime    = attachments.RuntimeOf(fromServer)!;

            await Assert.That(runtime.Pid).IsEqualTo(child.Id);

            await runtime.RequestGracefulStopAsync();
            await runtime.TerminateAsync(TimeSpan.Zero);
            await orch.HandleStopAgentForTest(fromServer);

            using var answer = new MemoryStream();
            await orch.HandleLocalStopV2Async(force: false, fromCli, answer, CancellationToken.None);
            answer.Position = 0;

            await Assert.That((await FrameCodec.ReadAsync(answer, CancellationToken.None))!.Text).IsEqualTo($"{fromCli}\tstopped");
            await Assert.That(attachments.Holds(fromServer)).IsFalse();
            await Assert.That(attachments.Holds(fromCli)).IsFalse();
            await Assert.That(server.AgentUnregisteredCalls).IsEquivalentTo([fromServer, fromCli]);
            await Assert.That(child.WaitForExit(500)).IsFalse();
        } finally {
            child.Kill();
            await child.WaitForExitAsync();
        }
    }

    /// <summary>A when-idle restart waits for every hosted agent, and an attached one is not hosted: it fires with an
    /// attachment live and a message waiting for its turn, as a turn-boundary one does.</summary>
    [Test]
    public async Task An_attached_agent_does_not_hold_up_a_restart() {
        var server = new CaptureServerConnection();
        await using var attachments = Attachments(server);
        await using var orch = Orchestrator(server, attachments);
        var agentId = await AttachAsync(attachments);
        Push(attachments, agentId, Guid.NewGuid());

        var whenIdle = new SpyStrategy();
        var atTurn   = new SpyStrategy();

        await Assert.That(RestartCoordinator.ForTest(Daemons.Store, "test", "v1", whenIdle, TimeProvider.System, orch, new EvalContextCache(TimeProvider.System))
            .RequestRestart(force: false, whenIdle: true)).IsEqualTo(RestartRequestResult.Restarting);
        await Assert.That(RestartCoordinator.ForTest(Daemons.Store, "test", "v1", atTurn, TimeProvider.System, orch, new EvalContextCache(TimeProvider.System))
            .RequestRestart(force: false, atTurnBoundary: true)).IsEqualTo(RestartRequestResult.Restarting);
        await Assert.That(whenIdle.Calls + atTurn.Calls).IsEqualTo(2);
    }

    /// <summary>A held dispatch moves only when the mod is idle with nothing outstanding and the server says so. A poll
    /// mid-turn and a poll while the mod holds a dispatch ask nothing; a hold, an outcome this daemon does not know and
    /// no answer at all keep it held.</summary>
    [Test]
    public async Task Next_returns_nothing_until_the_server_confirms() {
        var server = new CaptureServerConnection { HandoverAnswer = _ => new ConfirmInputHandoverResponse(InputHandoverOutcome.Hold) };
        await using var attachments = Attachments(server);
        var agentId  = await AttachAsync(attachments);
        var dispatch = Guid.NewGuid();
        Push(attachments, agentId, dispatch, "do the thing");

        await Assert.That(await attachments.NextAsync(agentId, Key, idle: false, outstanding: null)).IsNull();
        await Assert.That(await attachments.NextAsync(agentId, Key, idle: true, outstanding: Guid.NewGuid())).IsNull();
        await Assert.That(server.HandoverRequests).IsEmpty();

        await Assert.That(await IdleAsync(attachments, agentId)).IsNull();

        server.HandoverAnswer = _ => new ConfirmInputHandoverResponse("something_new");
        await Assert.That(await IdleAsync(attachments, agentId)).IsNull();

        server.HandoverAnswer = _ => null;
        await Assert.That(await IdleAsync(attachments, agentId)).IsNull();

        await Assert.That(server.HandoverRequests).IsEquivalentTo(Enumerable.Repeat(new ConfirmInputHandoverRequest(dispatch, agentId), 3));

        server.HandoverAnswer = _ => new ConfirmInputHandoverResponse(InputHandoverOutcome.HandedOver);
        await Assert.That(await IdleAsync(attachments, agentId)).IsEqualTo(SteeringNextReply.Handover(dispatch, "do the thing"));
        await Assert.That(await IdleAsync(attachments, agentId)).IsNull();
    }

    [Test]
    public async Task A_drop_moves_on_to_the_next_held_dispatch_in_the_same_poll() {
        var settled = Guid.NewGuid();
        var server  = new CaptureServerConnection {
            HandoverAnswer = r => new ConfirmInputHandoverResponse(r.DispatchId == settled ? InputHandoverOutcome.Drop : InputHandoverOutcome.HandedOver)
        };
        await using var attachments = Attachments(server);
        var agentId = await AttachAsync(attachments);
        var next    = Guid.NewGuid();
        Push(attachments, agentId, settled, "already rejected");
        Push(attachments, agentId, next, "still wanted");

        await Assert.That(await IdleAsync(attachments, agentId)).IsEqualTo(SteeringNextReply.Handover(next, "still wanted"));
        await Assert.That(server.HandoverRequests.Select(r => r.DispatchId)).IsEquivalentTo([settled, next]);
    }

    [Test]
    public async Task Held_dispatches_are_dropped_when_the_connection_drops() {
        var server = new CaptureServerConnection();
        await using var attachments = Attachments(server);
        var agentId = await AttachAsync(attachments);
        Push(attachments, agentId, Guid.NewGuid());

        server.RaiseConnectionLost();

        await Assert.That(await IdleAsync(attachments, agentId)).IsNull();
        await Assert.That(server.HandoverRequests).IsEmpty();

        var pushedAfter = Guid.NewGuid();
        Push(attachments, agentId, pushedAfter);

        await Assert.That((await IdleAsync(attachments, agentId))!.DispatchId).IsEqualTo(pushedAfter.ToString("D"));
    }

    /// <summary>The server rejects what it pushed on a connection that has gone, so a dispatch held from one never goes
    /// to the mod even when no drop was seen.</summary>
    [Test]
    public async Task A_dispatch_pushed_on_an_earlier_connection_is_never_handed_over() {
        var server = new CaptureServerConnection { ConnectionIdForTest = "connection-1" };
        await using var attachments = Attachments(server);
        var agentId = await AttachAsync(attachments);
        Push(attachments, agentId, Guid.NewGuid());

        server.ConnectionIdForTest = "connection-2";

        await Assert.That(await IdleAsync(attachments, agentId)).IsNull();
        await Assert.That(server.HandoverRequests).IsEmpty();
    }

    /// <summary>Shut down without a word to the server, then read back by the next daemon process, which reports the
    /// attachment live on connect and registers it again on the new connection.</summary>
    [Test]
    public async Task Attachments_survive_a_restart_and_are_reported_on_reconnect() {
        var before = new CaptureServerConnection();
        string first, second;

        await using (var attachments = Attachments(before)) {
            first  = await AttachAsync(attachments);
            second = await AttachAsync(attachments, session: Later, key: OtherKey);
        }

        await Assert.That(before.AgentUnregisteredCalls).IsEmpty();

        var after = new CaptureServerConnection();
        await using var restarted = Attachments(after);
        await using var orch = Orchestrator(after, restarted);

        var recovery = orch.TakeRecoverySnapshot();

        await Assert.That(recovery.LiveIds).Contains(first).And.Contains(second);
        await Assert.That(recovery.LiveAgents.Select(a => a.Id)).Contains(first).And.Contains(second);

        await orch.ReRegisterAgentsAsync();

        await Assert.That(after.AttachedRegistrations.Select(r => (r.AgentId, r.SessionId!, r.ProcessKey!, r.RuntimeTransport!)))
            .IsEquivalentTo([(first, Session, Key, "mod"), (second, Later, OtherKey, "mod")]);
        await Assert.That(await IdleAsync(restarted, first)).IsNull();
    }

    /// <summary>A mod gone silent past the lapse bound can report nothing more: what it was handed is reported
    /// Unconfirmed under the agent it went to, and the attachment is unregistered, which settles what the server holds
    /// for it. A beat mid-turn, with nothing to hand over, keeps it.</summary>
    [Test]
    public async Task A_lapsed_attachment_marks_the_outstanding_dispatch_unconfirmed() {
        var server = new CaptureServerConnection();
        await using var attachments = Attachments(server);
        var agentId  = await AttachAsync(attachments);
        var dispatch = Guid.NewGuid();
        Push(attachments, agentId, dispatch);
        await IdleAsync(attachments, agentId);
        var waiting = Guid.NewGuid();
        Push(attachments, agentId, waiting);

        _time.Advance(ModAttachments.LapseBound - TimeSpan.FromSeconds(1));
        await attachments.NextAsync(agentId, Key, idle: false, outstanding: dispatch);
        _time.Advance(ModAttachments.LapseBound - TimeSpan.FromSeconds(1));
        await attachments.SweepAsync();

        await Assert.That(attachments.Holds(agentId)).IsTrue();
        await Assert.That(server.UnconfirmedReports).IsEmpty();

        _time.Advance(TimeSpan.FromSeconds(2));
        await attachments.SweepAsync();

        await Assert.That(server.UnconfirmedReports).IsEquivalentTo([new ReportInputUnconfirmedRequest(dispatch, agentId, "attachment_lapsed")]);
        await Assert.That(server.AgentUnregisteredCalls).IsEquivalentTo([agentId]);
        await Assert.That(await IdleAsync(attachments, agentId)).IsEqualTo(SteeringNextReply.GoneAs("lapsed", retry: true));
        await Assert.That(server.HandoverRequests.Select(r => r.DispatchId)).DoesNotContain(waiting);
    }

    [Test]
    public async Task A_switch_off_daemon_refuses_attach() {
        var server = new CaptureServerConnection();
        await using var attachments = Attachments(server, steeringOn: static () => false);

        await Assert.That(await attachments.AttachAsync(Session, Key, null, CancellationToken.None))
            .IsEqualTo(SteeringAttachReply.Refusal("steering_off", retry: false));
        await Assert.That(server.AttachedRegistrations).IsEmpty();
    }

    /// <summary>As a stop does, but nothing is barred once the switch is on again.</summary>
    [Test]
    public async Task Turning_the_switch_off_ends_every_attachment() {
        var consent = new LaunchConsentStore(Daemons.Store.StateDirectory("test"), NullLogger.Instance, TimeProvider.System);
        consent.TrySetSteering(true, out _);

        var server = new CaptureServerConnection();
        await using var attachments = Attachments(server, steeringOn: () => consent.SteeringOn);
        await using var endpoint = new SteeringEndpoint(Daemons.Store.SteeringSocketPath("test"), TestSteering.NoRoutes, TimeProvider.System, NullLogger.Instance);
        using var steering = new SteeringSwitch(consent, endpoint, offered: true, attachments);

        var first  = await AttachAsync(attachments);
        var second = await AttachAsync(attachments, session: Later, key: OtherKey);

        using (var ack = new MemoryStream()) await steering.HandleSetAsync(SteeringIpc.Off, ack, CancellationToken.None);

        await Assert.That(attachments.Live).IsEmpty();
        await Assert.That(server.AgentUnregisteredCalls).IsEquivalentTo([first, second]);

        consent.TrySetSteering(true, out _);

        await Assert.That(await AttachAsync(attachments)).IsEqualTo(first);
    }

    /// <summary>Who records a session has no say in whether it can be steered: here a watcher holds the transcript,
    /// with no daemon claim on it, and the session attaches.</summary>
    [Test]
    public async Task A_session_on_a_fallback_watcher_can_still_attach() {
        var watchers = new WatcherPaths(Daemons.CreateDir("watchers"));
        var key      = SessionIds.Canonical(Session)!;
        File.WriteAllText(watchers.PidFile(key), $"{Environment.ProcessId}\n");

        await Assert.That(WatcherLiveness.PidAlive(watchers, key)).IsTrue();
        await Assert.That(new RecorderClaims(watchers.Directory, _time).Read(key)).IsEqualTo(RecorderClaimState.None);

        var server = new CaptureServerConnection();
        await using var attachments = Attachments(server);
        await using var orch = Orchestrator(server, attachments);

        await Assert.That((await attachments.AttachAsync(Session, Key, null, CancellationToken.None)).AgentId).IsEqualTo(AgentOf(Session));
        await Assert.That(server.AttachedRegistrations.Single().SessionId).IsEqualTo(Session);
    }

    [Test]
    public async Task Attach_passes_the_process_key_to_the_server() {
        var server = new CaptureServerConnection();
        await using var attachments = Attachments(server);

        var agentId = await AttachAsync(attachments);
        var sent    = server.AttachedRegistrations.Single();

        await Assert.That(ModAttachments.IsAgentId(agentId)).IsTrue();
        await Assert.That(sent.AgentId).IsEqualTo(agentId);
        await Assert.That(sent.ProcessKey).IsEqualTo(Key);
        await Assert.That(sent.SessionId).IsEqualTo(Session);
        await Assert.That(sent.RuntimeTransport).IsEqualTo("mod");
        await Assert.That(sent.Vendor).IsEqualTo("claude");
        await Assert.That(sent.ReplacesAgentId).IsNull();
        await Assert.That(sent.Prompt).IsNull();

        var options = new JsonSerializerOptions();
        ServerConnection.ConfigureHubPayload(options);
        var json = JsonSerializer.Serialize(sent, options);

        await Assert.That(json).Contains($"\"process_key\":\"{Key}\"");
        await Assert.That(json).Contains($"\"session_id\":\"{Session}\"");
        await Assert.That(json).DoesNotContain("replaces_agent_id");
    }

    /// <summary>Through the seam the platform check takes, so it runs everywhere: where steering is not offered the
    /// capability is not advertised and nothing attaches.</summary>
    [Test]
    public async Task Steering_is_refused_on_windows() {
        var config   = new DaemonConfig { Name = "test", ServerUrl = "http://127.0.0.1:1", ConfigRoot = Config.Root };
        var offered  = new CaptureServerConnection(config);
        var withheld = new CaptureServerConnection(config);
        await using var steered    = Attachments(offered, offered: true);
        await using var notSteered = Attachments(withheld, offered: false);

        await Assert.That(offered.BuildDaemonConnect("mac", [], [], null, config.Advertisement).Capabilities!).IsEquivalentTo(["steering/1"]);
        await Assert.That(withheld.BuildDaemonConnect("win", [], [], null, config.Advertisement).Capabilities).IsNull();
        await Assert.That(await notSteered.AttachAsync(Session, Key, null, CancellationToken.None))
            .IsEqualTo(SteeringAttachReply.Refusal("steering_off", retry: false));
        await Assert.That(withheld.AttachedRegistrations).IsEmpty();
    }

    /// <summary>The server holds the dispatch With the mod, the mod has no record of it, and the mod records a dispatch
    /// before it submits one: that is the proof it cannot land, so it is reported dropped, and the next poll hands the
    /// held one over. A mod that does hold it only beats.</summary>
    [Test]
    public async Task A_hold_behind_a_dispatch_the_mod_does_not_hold_drops_it() {
        var server = new CaptureServerConnection();
        await using var attachments = Attachments(server);
        var agentId = await AttachAsync(attachments);
        var lost    = Guid.NewGuid();
        Push(attachments, agentId, lost);
        await IdleAsync(attachments, agentId);

        var next = Guid.NewGuid();
        Push(attachments, agentId, next, "next one");
        server.HandoverAnswer = r => server.AttachedRejections.Count == 0
            ? new ConfirmInputHandoverResponse(InputHandoverOutcome.Hold, lost)
            : new ConfirmInputHandoverResponse(InputHandoverOutcome.HandedOver);

        await Assert.That(await attachments.NextAsync(agentId, Key, idle: true, outstanding: lost)).IsNull();
        await Assert.That(server.AttachedRejections).IsEmpty();

        await Assert.That(await IdleAsync(attachments, agentId)).IsNull();
        await Assert.That(server.AttachedRejections).IsEquivalentTo([(lost, agentId, "not_held_by_mod")]);

        await Assert.That(await IdleAsync(attachments, agentId)).IsEqualTo(SteeringNextReply.Handover(next, "next one"));
    }

    /// <summary>With no record here of which agent the blocking dispatch went to, as after the record was lost, the agent
    /// polling and the one its process carried on from are tried until a stream takes the report.</summary>
    [Test]
    public async Task A_lost_dispatch_with_no_record_here_is_reported_where_a_stream_takes_it() {
        var server = new CaptureServerConnection();
        await using var attachments = Attachments(server);
        var first   = AgentOf(Session);
        var current = await AttachAsync(attachments, session: Later, replaces: first);
        var lost    = Guid.NewGuid();
        Push(attachments, current, Guid.NewGuid());
        server.HandoverAnswer          = _ => new ConfirmInputHandoverResponse(InputHandoverOutcome.Hold, lost);
        server.AttachedRejectionRecorded = (_, agentId) => agentId == first;

        await IdleAsync(attachments, current);

        await Assert.That(server.AttachedRejections).IsEquivalentTo([(lost, current, "not_held_by_mod"), (lost, first, "not_held_by_mod")]);
    }

    /// <summary>After <c>/clear</c> the mod reports under the agent it attached as since, but the dispatch was sent to the
    /// agent before, whose input stream alone holds it.</summary>
    [Test]
    public async Task A_link_is_reported_under_the_agent_the_dispatch_was_sent_to() {
        var server = new CaptureServerConnection();
        await using var attachments = Attachments(server);
        var first      = await AttachAsync(attachments);
        var beforeClear = Guid.NewGuid();
        Push(attachments, first, beforeClear);
        await IdleAsync(attachments, first);

        var second     = await AttachAsync(attachments, session: Later, replaces: first);
        var afterClear = Guid.NewGuid();
        Push(attachments, second, afterClear);
        await IdleAsync(attachments, second);

        var row = Guid.NewGuid();
        attachments.Link(second, Key, beforeClear, Later, row);
        attachments.Undelivered(second, Key, afterClear, rejected: false, "no_row");
        await attachments.FlushReportsAsync();

        await Assert.That(server.LinkReports).IsEquivalentTo([new ReportInputLinkRequest(beforeClear, first, Later, row)]);
        await Assert.That(server.UnconfirmedReports).IsEquivalentTo([new ReportInputUnconfirmedRequest(afterClear, second, "no_row")]);
    }

    [Test]
    public async Task A_rejection_from_the_mod_is_reported_with_its_reason() {
        var server = new CaptureServerConnection();
        await using var attachments = Attachments(server);
        var agentId  = await AttachAsync(attachments);
        var dispatch = Guid.NewGuid();
        Push(attachments, agentId, dispatch);
        await IdleAsync(attachments, agentId);

        attachments.Undelivered(agentId, Key, dispatch, rejected: true, "stopped");
        await attachments.FlushReportsAsync();

        await Assert.That(server.AttachedRejections).IsEquivalentTo([(dispatch, agentId, "stopped")]);
    }

    /// <summary>The server ended the replaced agent when its replacement registered: it is never unregistered here, never
    /// announced again, and the process that left it never attaches as it again.</summary>
    [Test]
    public async Task A_replaced_agent_is_never_unregistered_or_reattached() {
        var server = new CaptureServerConnection();
        await using var attachments = Attachments(server);
        var first  = await AttachAsync(attachments);
        var second = await AttachAsync(attachments, session: Later, replaces: first);

        await Assert.That(attachments.Holds(first)).IsFalse();

        await attachments.DetachAsync(first, Key);

        await Assert.That(server.AgentUnregisteredCalls).IsEmpty();
        await Assert.That(await IdleAsync(attachments, first)).IsEqualTo(SteeringNextReply.GoneAs("replaced", retry: false));

        server.AttachedRegistrations.Clear();
        await attachments.ReRegisterAsync();

        await Assert.That(server.AttachedRegistrations.Select(r => (r.AgentId, r.ReplacesAgentId))).IsEquivalentTo([(second, (string?)first)]);

        server.AttachedRegistrations.Clear();

        await Assert.That(await attachments.AttachAsync(Session, Key, null, CancellationToken.None))
            .IsEqualTo(SteeringAttachReply.Refusal("replaced", retry: false));
        await Assert.That(server.AttachedRegistrations).IsEmpty();

        server.AttachedRegistrationAnswer = r => r.AgentId == first ? new AgentRegisteredResponse(false, "session_attached") : null;

        await Assert.That(await attachments.AttachAsync(Session, OtherKey, null, CancellationToken.None))
            .IsEqualTo(SteeringAttachReply.Refusal("session_attached", retry: false));
    }

    [Test]
    public async Task A_session_the_daemon_hosts_is_refused() {
        var server = new CaptureServerConnection();
        await using var attachments = Attachments(server);
        await using var orch = Orchestrator(server, attachments);
        var hosted = orch.SeedAgentForTest("hosted-1", vendor: "claude");
        hosted.SessionId = Session;
        hosted.MovedTo.TryAdd(SessionIds.Canonical(Later)!, 0);

        await Assert.That(await attachments.AttachAsync(Session.ToUpperInvariant(), Key, null, CancellationToken.None))
            .IsEqualTo(SteeringAttachReply.Refusal("hosted", retry: false));
        await Assert.That(await attachments.AttachAsync(Later, Key, null, CancellationToken.None))
            .IsEqualTo(SteeringAttachReply.Refusal("hosted", retry: false));
        await Assert.That(server.AttachedRegistrations).IsEmpty();
    }

    /// <summary>A hosted Claude with the mod loaded can attach before the daemon has found its session, so its process
    /// is recognized too.</summary>
    [Test]
    public async Task A_process_the_daemon_hosts_is_refused_before_its_session_is_known() {
        var server = new CaptureServerConnection();
        await using var attachments = Attachments(server);
        await using var orch = Orchestrator(server, attachments);
        var pty = new RecordingPtyProcess();
        orch.SeedAgentForTest("hosted-1", vendor: "claude", pty: pty);

        await Assert.That(await attachments.AttachAsync(Session, $"{pty.Pid}:start", null, CancellationToken.None))
            .IsEqualTo(SteeringAttachReply.Refusal("hosted", retry: false));
        await Assert.That(server.AttachedRegistrations).IsEmpty();
    }

    /// <summary>A link the server never took is kept, through a dropped connection and a daemon restart, and sent once
    /// the server is there again; sent once, it is gone.</summary>
    [Test]
    public async Task An_unacknowledged_link_is_retried_after_a_reconnect() {
        var server   = new CaptureServerConnection();
        var dispatch = Guid.NewGuid();
        var row      = Guid.NewGuid();
        string agentId;

        await using (var attachments = Attachments(server)) {
            agentId = await AttachAsync(attachments);
            Push(attachments, agentId, dispatch);
            await IdleAsync(attachments, agentId);

            server.AttachedReportThrow = new IOException("connection reset");
            attachments.Link(agentId, Key, dispatch, Session, row);
            await attachments.FlushReportsAsync();

            server.AttachedReportThrow = null;
            server.Ready               = false;
            await attachments.SweepAsync();

            await Assert.That(server.LinkReports).IsEmpty();
        }

        var reconnected = new CaptureServerConnection();
        await using var restarted = Attachments(reconnected);

        await restarted.SweepAsync();
        await restarted.SweepAsync();

        await Assert.That(reconnected.LinkReports).IsEquivalentTo([new ReportInputLinkRequest(dispatch, agentId, Session, row)]);
    }

    /// <summary>The count the server reads for capacity is hosted agents only, on every carrier of it.</summary>
    [Test]
    public async Task Attachments_are_left_out_of_the_active_count() {
        var server = new CaptureServerConnection();
        await using var attachments = Attachments(server);
        await using var orch = Orchestrator(server, attachments);
        orch.SeedAgentForTest("hosted-1", status: "Running");

        var agentId = await AttachAsync(attachments);
        var report  = orch.BuildStatusReport();

        await Assert.That(orch.ActiveCountSnapshot().Count).IsEqualTo(1);
        await Assert.That(server.AttachedRegistrations.Single().ActiveCount).IsEqualTo(1);
        await Assert.That(report.ActiveCount).IsEqualTo(1);
        await Assert.That(report.LiveAgents.Select(a => a.Id)).Contains(agentId);
    }

    [Test]
    [Arguments("session_attached", true)]
    [Arguments("session_not_owned", true)]
    [Arguments("connection_vanished", true)]
    [Arguments("steering_unsupported", false)]
    [Arguments("invalid_attachment", false)]
    [Arguments("replacement_refused", false)]
    [Arguments("something_new", false)]
    public async Task A_refusal_tells_the_mod_whether_to_attach_again(string refusal, bool retry) {
        var server = new CaptureServerConnection { AttachedRegistrationAnswer = _ => new AgentRegisteredResponse(false, refusal) };
        await using var attachments = Attachments(server);

        await Assert.That(await attachments.AttachAsync(Session, Key, null, CancellationToken.None)).IsEqualTo(SteeringAttachReply.Refusal(refusal, retry));
        await Assert.That(attachments.Live).IsEmpty();
    }

    static Task<ConfirmInputHandoverResponse?> NoHandoverMethod() =>
        Task.FromException<ConfirmInputHandoverResponse?>(
            new HubException("Failed to invoke 'ConfirmInputHandover' due to an error on the server. HubException: Method does not exist."));

    /// <summary>A server from before attached agents has <c>AgentRegisteredAcked</c>, and would take an attached agent's
    /// registration for a hosted one's and drop everything sent to it, but has no <c>ConfirmInputHandover</c>. Asked
    /// about the empty dispatch, it says it can't steer: nothing registers, and the mod comes back slowly in case the
    /// server is upgraded.</summary>
    [Test]
    public async Task A_server_without_the_handover_method_registers_no_attached_agent() {
        var server = new CaptureServerConnection { SteeringProbe = NoHandoverMethod };
        await using var attachments = Attachments(server);

        await Assert.That(await attachments.AttachAsync(Session, Key, null, CancellationToken.None))
            .IsEqualTo(SteeringAttachReply.Refusal("steering_unsupported", retry: true));
        await Assert.That(server.AttachedRegistrations).IsEmpty();
        await Assert.That(attachments.Live).IsEmpty();
    }

    /// <summary>Until the server has said whether it steers, nothing registers, and the mod is told to try again.</summary>
    [Test]
    public async Task An_attach_before_the_server_says_whether_it_steers_registers_nothing() {
        var server = new CaptureServerConnection { SteeringProbe = () => Task.FromException<ConfirmInputHandoverResponse?>(new IOException("the connection went")) };
        await using var attachments = Attachments(server);

        await Assert.That(await attachments.AttachAsync(Session, Key, null, CancellationToken.None))
            .IsEqualTo(SteeringAttachReply.Refusal("unreachable", retry: true));
        await Assert.That(server.AttachedRegistrations).IsEmpty();
    }

    /// <summary>The server is asked once for its connection, not once per attach.</summary>
    [Test]
    public async Task The_server_is_asked_once_per_connection_whether_it_steers() {
        var server = new CaptureServerConnection();
        await using var attachments = Attachments(server);

        await AttachAsync(attachments);
        await AttachAsync(attachments, session: Later, key: OtherKey);
        await Assert.That(server.SteeringProbes).IsEqualTo(1);

        server.ConnectionIdForTest = "connection-2";
        await AttachAsync(attachments, key: OtherKey);
        await Assert.That(server.SteeringProbes).IsEqualTo(2);
    }

    /// <summary>An attachment kept across a restart registers again only on a server that steers: on one that can't, the
    /// re-registration and the sweep send nothing, and the mod is told to come back slowly.</summary>
    [Test]
    public async Task Kept_attachments_do_not_register_again_on_a_server_that_cannot_steer() {
        string agentId;
        await using (var before = Attachments(new CaptureServerConnection())) agentId = await AttachAsync(before);

        var server = new CaptureServerConnection { SteeringProbe = NoHandoverMethod };
        await using var restarted = Attachments(server);

        await restarted.ReRegisterAsync();
        await restarted.SweepAsync();

        await Assert.That(server.AttachedRegistrations).IsEmpty();
        await Assert.That(await IdleAsync(restarted, agentId)).IsEqualTo(SteeringNextReply.GoneAs("steering_unsupported", retry: true));
    }

    /// <summary>Every hosted agent's readiness waits behind the re-registration, so the attachments register all at once,
    /// and the wait has one bound however slow the server is.</summary>
    [Test]
    public async Task Kept_attachments_register_again_together_within_one_bound() {
        await using (var before = Attachments(new CaptureServerConnection())) {
            await AttachAsync(before);
            await AttachAsync(before, session: Later, key: OtherKey);
        }

        var gate   = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = new CaptureServerConnection { AttachedRegistrationGate = gate };
        await using var restarted = Attachments(server);

        var registering = restarted.ReRegisterAsync();
        var deadline    = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        int Registered() { lock (server.AttachedRegistrations) return server.AttachedRegistrations.Count; }
        while (Registered() < 2 && DateTime.UtcNow < deadline) await Task.Delay(10);

        await Assert.That(Registered()).IsEqualTo(2);

        _time.Advance(TimeSpan.FromSeconds(15));
        await registering.WithinDeadlineAsync("the re-registration's bound");
        gate.SetResult();
    }

    [Test]
    public async Task A_daemon_with_no_server_connection_says_to_attach_again() {
        var server = new CaptureServerConnection { Ready = false };
        await using var attachments = Attachments(server);

        await Assert.That(await attachments.AttachAsync(Session, Key, null, CancellationToken.None))
            .IsEqualTo(SteeringAttachReply.Refusal("unreachable", retry: true));
        await Assert.That(server.AttachedRegistrations).IsEmpty();
    }

    /// <summary>A module reload or a daemon restart attaches the same process again: the same agent id, whichever
    /// spelling of the session it sends, and what was held for it is still there.</summary>
    [Test]
    public async Task The_same_process_attaching_again_keeps_its_agent_and_what_is_held() {
        var server = new CaptureServerConnection();
        await using var attachments = Attachments(server);
        var agentId  = await AttachAsync(attachments);
        var dispatch = Guid.NewGuid();
        Push(attachments, agentId, dispatch);

        await Assert.That(await AttachAsync(attachments, session: Session.Replace("-", "", StringComparison.Ordinal))).IsEqualTo(agentId);
        await Assert.That((await IdleAsync(attachments, agentId))!.DispatchId).IsEqualTo(dispatch.ToString("D"));
    }

    [Test]
    public async Task A_stopped_session_and_process_are_refused_until_one_changes() {
        var server = new CaptureServerConnection();
        await using var attachments = Attachments(server);
        var agentId = await AttachAsync(attachments);

        attachments.Stop(agentId);

        await Assert.That(await attachments.AttachAsync(Session, Key, null, CancellationToken.None))
            .IsEqualTo(SteeringAttachReply.Refusal("stopped", retry: false));
        await Assert.That(await IdleAsync(attachments, agentId)).IsEqualTo(SteeringNextReply.GoneAs("stopped", retry: false));
        await Assert.That(await AttachAsync(attachments, key: OtherKey)).IsEqualTo(agentId);
        await Assert.That(await AttachAsync(attachments, session: Later)).IsEqualTo(AgentOf(Later));
    }

    [Test]
    public async Task A_stop_outlives_a_daemon_restart() {
        await using (var attachments = Attachments(new CaptureServerConnection())) attachments.Stop(await AttachAsync(attachments));

        await using var restarted = Attachments(new CaptureServerConnection());

        await Assert.That(await restarted.AttachAsync(Session, Key, null, CancellationToken.None))
            .IsEqualTo(SteeringAttachReply.Refusal("stopped", retry: false));
    }

    /// <summary>Two processes can run one session, so share its agent id: a poll from one that is not attached is told
    /// to attach again and reaches nothing held for the other.</summary>
    [Test]
    public async Task A_poll_from_another_process_reaches_nothing() {
        var server = new CaptureServerConnection();
        await using var attachments = Attachments(server);
        var agentId = await AttachAsync(attachments);
        Push(attachments, agentId, Guid.NewGuid());

        await Assert.That(await IdleAsync(attachments, agentId, OtherKey)).IsEqualTo(SteeringNextReply.GoneAs("lost", retry: true));
        await Assert.That(server.HandoverRequests).IsEmpty();

        await attachments.DetachAsync(agentId, OtherKey);
        await Assert.That(attachments.Holds(agentId)).IsTrue();
    }

    [Test]
    public async Task A_detached_attachment_is_unregistered_and_stays_gone() {
        var server = new CaptureServerConnection();
        await using var attachments = Attachments(server);
        var agentId = await AttachAsync(attachments);

        await attachments.DetachAsync(agentId, Key);

        await Assert.That(server.AgentUnregisteredCalls).IsEquivalentTo([agentId]);
        await Assert.That(await IdleAsync(attachments, agentId)).IsEqualTo(SteeringNextReply.GoneAs("detached", retry: false));
        await Assert.That(await IdleAsync(attachments, AgentOf(Later))).IsEqualTo(SteeringNextReply.GoneAs("lost", retry: true));
    }

    /// <summary>A reconnect that finds another process on the session ends this attachment, and its mod is told so and to
    /// try again on its next poll.</summary>
    [Test]
    public async Task An_attachment_the_server_refuses_on_reconnect_ends_and_says_why() {
        var server = new CaptureServerConnection();
        await using var attachments = Attachments(server);
        var agentId = await AttachAsync(attachments);

        server.AttachedRegistrationAnswer = _ => new AgentRegisteredResponse(false, "session_attached");
        await attachments.ReRegisterAsync();

        await Assert.That(attachments.Holds(agentId)).IsFalse();
        await Assert.That(await IdleAsync(attachments, agentId)).IsEqualTo(SteeringNextReply.GoneAs("session_attached", retry: true));
        await Assert.That(server.AgentUnregisteredCalls).IsEmpty();
    }

    /// <summary>The mod submits text alone, so a message with files is refused at once rather than delivered without
    /// them.</summary>
    [Test]
    public async Task Input_with_files_is_refused_rather_than_held() {
        var server = new CaptureServerConnection();
        await using var attachments = Attachments(server);
        var agentId  = await AttachAsync(attachments);
        var dispatch = Guid.NewGuid();

        await Assert.That(attachments.TakeInput(new SendInputCommand(agentId, "see attached", ["att-1"], dispatch))).IsTrue();

        await Assert.That(server.AttachedRejections).IsEquivalentTo([(dispatch, agentId, "attachments_unsupported")]);
        await Assert.That(await IdleAsync(attachments, agentId)).IsNull();
    }

    /// <summary>Input for an attachment never reaches the hosted delivery path, which would report its agent unknown.</summary>
    [Test]
    public async Task Server_input_for_an_attachment_is_held_for_its_mod() {
        var server = new CaptureServerConnection();
        await using var attachments = Attachments(server);
        await using var orch = Orchestrator(server, attachments);
        var agentId  = await AttachAsync(attachments);
        var dispatch = Guid.NewGuid();

        await orch.HandleSendInputForTest(new SendInputCommand(agentId, "from the phone", null, dispatch));

        await Assert.That(server.InputRejections).IsEmpty();
        await Assert.That(await IdleAsync(attachments, agentId)).IsEqualTo(SteeringNextReply.Handover(dispatch, "from the phone"));
    }

    [Test]
    public async Task An_attached_agent_is_listed_as_attached_not_hosted() {
        var server = new CaptureServerConnection();
        await using var attachments = Attachments(server);
        await using var orch = Orchestrator(server, attachments);
        var agentId = await AttachAsync(attachments);

        using var list = new MemoryStream();
        await orch.HandleLocalListAsync(withTitles: true, withState: true, list, CancellationToken.None);
        list.Position = 0;

        var row = orch.SnapshotAgentsForStatus().Single();

        await Assert.That((await FrameCodec.ReadAsync(list, CancellationToken.None))!.Text).IsEqualTo($"{agentId}\tRunning\t\tattached\t\t\t\tattached");
        await Assert.That((row.Id, row.Kind, row.Vendor, row.HasTerminal, row.SessionId))
            .IsEqualTo((agentId, "attached", "claude", (bool?)false, SessionIds.Canonical(Session)));
    }

    static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    static Task Within(Task task) => task.WaitAsync(TimeSpan.FromSeconds(10));

    static Task<T> Within<T>(Task<T> task) => task.WaitAsync(TimeSpan.FromSeconds(10));

    /// <summary>A link the daemon holds but has not delivered is the mod's record of the dispatch: a hold behind it sends
    /// the link, never a rejection that would tell the sender to resend what landed.</summary>
    [Test]
    public async Task A_hold_behind_a_dispatch_whose_report_is_still_queued_sends_the_report_not_a_rejection() {
        var server = new CaptureServerConnection();
        await using var attachments = Attachments(server);
        var agentId = await AttachAsync(attachments);
        var landed  = Guid.NewGuid();
        Push(attachments, agentId, landed);
        await IdleAsync(attachments, agentId);

        var row = Guid.NewGuid();
        server.Ready = false;
        attachments.Link(agentId, Key, landed, Session, row);
        var next = Guid.NewGuid();
        Push(attachments, agentId, next, "next one");
        server.Ready = true;

        server.HandoverAnswer = _ => server.LinkReports.Count == 0
            ? new ConfirmInputHandoverResponse(InputHandoverOutcome.Hold, landed)
            : new ConfirmInputHandoverResponse(InputHandoverOutcome.HandedOver);

        await Assert.That(await IdleAsync(attachments, agentId)).IsNull();
        await Assert.That(server.AttachedRejections).IsEmpty();
        await Assert.That(server.LinkReports).IsEquivalentTo([new ReportInputLinkRequest(landed, agentId, Session, row)]);
        await Assert.That(await IdleAsync(attachments, agentId)).IsEqualTo(SteeringNextReply.Handover(next, "next one"));
    }

    [Test]
    public async Task Reports_go_as_soon_as_the_connection_is_registered_again() {
        var server = new CaptureServerConnection();
        await using var attachments = Attachments(server);
        var agentId  = await AttachAsync(attachments);
        var dispatch = Guid.NewGuid();
        var row      = Guid.NewGuid();

        server.Ready = false;
        attachments.Link(agentId, Key, dispatch, Session, row);
        server.Ready = true;
        server.RaiseRegistered();

        await Assert.That(server.LinkReports).IsEquivalentTo([new ReportInputLinkRequest(dispatch, agentId, Session, row)]);
    }

    /// <summary>A module reload's attach is in its registration when <c>kcap agent stop</c> lands: the attach is refused
    /// once the server answers it, and the server's last word is the unregistration, so the stopped pair is not attached
    /// again, now or on the next reconnect.</summary>
    [Test]
    public async Task A_stop_landing_during_an_attach_is_not_undone() {
        var server = new CaptureServerConnection();
        await using var attachments = Attachments(server);
        var agentId = await AttachAsync(attachments);
        var release = Gate();
        server.AttachedRegistrationGate = release;

        var reattach = attachments.AttachAsync(Session, Key, null, CancellationToken.None);
        await Within(server.AttachedRegistrationEntered.Task);

        await Assert.That(attachments.Stop(agentId)).IsTrue();

        release.SetResult();

        await Assert.That(await Within(reattach)).IsEqualTo(SteeringAttachReply.Refusal("stopped", retry: false));
        await Within(attachments.SweepAsync());
        await Assert.That(attachments.Holds(agentId)).IsFalse();
        await Assert.That(string.Join(",", server.AttachedCallOrder)).IsEqualTo($"register:{agentId},register:{agentId},unregister:{agentId}");

        server.AttachedRegistrationGate = null;
        server.AttachedRegistrations.Clear();
        await attachments.ReRegisterAsync();

        await Assert.That(server.AttachedRegistrations).IsEmpty();
        await Assert.That(await attachments.AttachAsync(Session, Key, null, CancellationToken.None))
            .IsEqualTo(SteeringAttachReply.Refusal("stopped", retry: false));
    }

    /// <summary>A reconnect is registering the attachment again when a stop lands: the unregistration waits for that
    /// registration, so the server's last word is the unregistration and the stopped attachment stays gone.</summary>
    [Test]
    public async Task A_stop_landing_during_a_reconnect_registration_is_not_undone() {
        var server = new CaptureServerConnection();
        await using var attachments = Attachments(server);
        var agentId = await AttachAsync(attachments);
        var release = Gate();
        server.AttachedRegistrationGate = release;

        var reregister = attachments.ReRegisterAsync();
        await Within(server.AttachedRegistrationEntered.Task);

        await Assert.That(attachments.Stop(agentId)).IsTrue();
        await Assert.That(server.AgentUnregisteredCalls).IsEmpty();

        release.SetResult();
        await Within(reregister);
        await Within(attachments.SweepAsync());

        await Assert.That(attachments.Holds(agentId)).IsFalse();
        await Assert.That(string.Join(",", server.AttachedCallOrder)).IsEqualTo($"register:{agentId},register:{agentId},unregister:{agentId}");
    }

    /// <summary>A server stop runs on the lane every launch and stop shares, so it ends the attachment and returns while
    /// an attach of the same agent still waits on the server; the server is told once that registration has settled.</summary>
    [Test]
    public async Task A_server_stop_returns_while_an_attach_of_its_agent_is_registering() {
        var server = new CaptureServerConnection();
        await using var attachments = Attachments(server);
        await using var orch = Orchestrator(server, attachments);
        var agentId = await AttachAsync(attachments);
        var release = Gate();
        server.AttachedRegistrationGate = release;

        var reattach = attachments.AttachAsync(Session, Key, null, CancellationToken.None);
        await Within(server.AttachedRegistrationEntered.Task);

        try {
            await Within(orch.HandleServerStopAgentForTest(agentId));

            await Assert.That(reattach.IsCompleted).IsFalse();
            await Assert.That(attachments.Holds(agentId)).IsFalse();
            await Assert.That(await IdleAsync(attachments, agentId)).IsEqualTo(SteeringNextReply.GoneAs("stopped", retry: false));
            await Assert.That(server.AgentUnregisteredCalls).IsEmpty();
        } finally {
            release.TrySetResult();
        }

        await Assert.That(await Within(reattach)).IsEqualTo(SteeringAttachReply.Refusal("stopped", retry: false));
        await Within(attachments.SweepAsync());
        await Assert.That(string.Join(",", server.AttachedCallOrder)).IsEqualTo($"register:{agentId},register:{agentId},unregister:{agentId}");
    }

    /// <summary>The switch answers the command that flips it off while an attach still waits on the server: that attach is
    /// refused once the server answers it, and the server is told the attachment ended once it has.</summary>
    [Test]
    public async Task Turning_the_switch_off_returns_while_an_attach_is_registering() {
        var consent = new LaunchConsentStore(Daemons.Store.StateDirectory("test"), NullLogger.Instance, TimeProvider.System);
        consent.TrySetSteering(true, out _);

        var server = new CaptureServerConnection();
        await using var attachments = Attachments(server, steeringOn: () => consent.SteeringOn);
        await using var endpoint = new SteeringEndpoint(Daemons.Store.SteeringSocketPath("test"), TestSteering.NoRoutes, TimeProvider.System, NullLogger.Instance);
        using var steering = new SteeringSwitch(consent, endpoint, offered: true, attachments);
        var agentId = await AttachAsync(attachments);
        var release = Gate();
        server.AttachedRegistrationGate = release;

        var reattach = attachments.AttachAsync(Session, Key, null, CancellationToken.None);
        await Within(server.AttachedRegistrationEntered.Task);

        try {
            using (var ack = new MemoryStream()) await Within(steering.HandleSetAsync(SteeringIpc.Off, ack, CancellationToken.None));

            await Assert.That(attachments.Live).IsEmpty();
            await Assert.That(server.AgentUnregisteredCalls).IsEmpty();
        } finally {
            release.TrySetResult();
        }

        await Assert.That(await Within(reattach)).IsEqualTo(SteeringAttachReply.Refusal("steering_off", retry: false));
        await Within(attachments.SweepAsync());
        await Assert.That(string.Join(",", server.AttachedCallOrder)).IsEqualTo($"register:{agentId},register:{agentId},unregister:{agentId}");
    }

    /// <summary>The mod wakes from a suspension while its lapse is being told to the server: its attach again waits until
    /// the unregistration is done, so the fresh attachment is not ended by it.</summary>
    [Test]
    public async Task A_lapse_and_an_attach_from_the_waking_mod_do_not_cross() {
        var server = new CaptureServerConnection();
        await using var attachments = Attachments(server);
        var agentId = await AttachAsync(attachments);
        var release = Gate();
        server.UnregisterGate = release;

        _time.Advance(ModAttachments.LapseBound + ModAttachments.SweepInterval);
        await Within(server.UnregisterEntered.Task);
        var sweep = attachments.SweepAsync();

        await Assert.That(await IdleAsync(attachments, agentId)).IsEqualTo(SteeringNextReply.GoneAs("lapsed", retry: true));

        var reattach = attachments.AttachAsync(Session, Key, null, CancellationToken.None);

        await Assert.That(reattach.IsCompleted).IsFalse();

        release.SetResult();
        await Within(sweep);

        await Assert.That((await reattach).AgentId).IsEqualTo(agentId);
        await Assert.That(attachments.Holds(agentId)).IsTrue();
        await Assert.That(string.Join(",", server.AttachedCallOrder)).IsEqualTo($"register:{agentId},unregister:{agentId},register:{agentId}");
    }

    /// <summary>A file another build wrote can parse and still be missing what an entry needs: each bad entry is dropped,
    /// the rest restored, and the sweep keeps working.</summary>
    [Test]
    public async Task A_state_file_with_bad_entries_keeps_the_good_ones() {
        var good     = AgentOf(Session);
        var dispatch = Guid.NewGuid();
        Daemons.CreateFile(Path.GetRelativePath(Daemons.Directory, StatePath), $$"""
            {
              "attachments": [null, { "sessionId": "{{Later}}", "processKey": "{{OtherKey}}", "attachedAt": "2026-11-08T09:00:00Z" },
                              { "agentId": "{{good}}", "sessionId": "{{Session}}", "processKey": "{{Key}}", "attachedAt": "2026-11-08T09:00:00Z" }],
              "handedOver": { "{{Guid.NewGuid()}}": null, "{{dispatch}}": { "agentId": "{{good}}", "processKey": "{{Key}}" } },
              "reports": [null, { "kind": "link" },
                          { "kind": "rejected", "dispatchId": "{{Guid.NewGuid()}}", "agentId": "{{good}}", "reason": "stopped", "refusals": -1 }],
              "replaced": { "not-an-agent": "{{Key}}" },
              "stopped": [null]
            }
            """);

        var server = new CaptureServerConnection();
        await using var attachments = Attachments(server);

        await Assert.That(attachments.Live.Select(a => a.AgentId)).IsEquivalentTo([good]);

        _time.Advance(ModAttachments.LapseBound + ModAttachments.SweepInterval);
        await Within(attachments.SweepAsync());

        await Assert.That(server.UnconfirmedReports).IsEquivalentTo([new ReportInputUnconfirmedRequest(dispatch, good, "attachment_lapsed")]);
        await Assert.That(server.AttachedRejections).IsEmpty();
        await Assert.That(server.AgentUnregisteredCalls).IsEquivalentTo([good]);
    }

    /// <summary>One made while the server is away, or one the server failed, is sent on a later sweep; one for an agent
    /// attached again meanwhile is not, since the server now holds that agent's new attachment.</summary>
    [Test]
    public async Task An_unregistration_that_could_not_be_sent_goes_on_a_later_sweep() {
        var server  = new CaptureServerConnection();
        await using var attachments = Attachments(server);
        var away     = await AttachAsync(attachments);
        var failed   = await AttachAsync(attachments, session: Later);
        var attached = await AttachAsync(attachments, session: "c56a4180-65aa-42ec-a945-5fd21dec0538");

        server.Ready = false;
        attachments.Stop(away);
        await attachments.DetachAsync(attached, Key);
        server.Ready = true;
        server.AgentUnregisteredThrow = new IOException("connection reset");
        attachments.Stop(failed);
        server.AgentUnregisteredThrow = null;

        await Assert.That(server.AgentUnregisteredCalls).IsEmpty();

        await AttachAsync(attachments, session: "c56a4180-65aa-42ec-a945-5fd21dec0538");
        await Within(attachments.SweepAsync());
        await Within(attachments.SweepAsync());

        await Assert.That(server.AgentUnregisteredCalls).IsEquivalentTo([away, failed]);
    }

    /// <summary>The server is told what became of a dispatch it reserved with files even when the first report of it is
    /// lost.</summary>
    [Test]
    public async Task A_refusal_of_files_is_kept_until_the_server_takes_it() {
        var server = new CaptureServerConnection { AttachedReportThrow = new IOException("connection reset") };
        await using var attachments = Attachments(server);
        var agentId  = await AttachAsync(attachments);
        var dispatch = Guid.NewGuid();

        attachments.TakeInput(new SendInputCommand(agentId, "see attached", ["att-1"], dispatch));
        server.AttachedReportThrow = null;
        await Within(attachments.SweepAsync());

        await Assert.That(server.AttachedRejections).IsEquivalentTo([(dispatch, agentId, "attachments_unsupported")]);
    }

    /// <summary>The server confirmed the handover while a stop ended the attachment: the dispatch has not reached the mod,
    /// so it is reported dropped and the poll is told the attachment is gone.</summary>
    [Test]
    public async Task A_handover_confirmed_after_a_stop_is_reported_dropped_rather_than_returned() {
        var server = new CaptureServerConnection();
        await using var attachments = Attachments(server);
        var agentId  = await AttachAsync(attachments);
        var dispatch = Guid.NewGuid();
        Push(attachments, agentId, dispatch);
        var release = Gate();
        server.HandoverGate = release;

        var poll = IdleAsync(attachments, agentId);
        await Within(server.HandoverEntered.Task);
        attachments.Stop(agentId);
        release.SetResult();

        await Assert.That(await poll).IsEqualTo(SteeringNextReply.GoneAs("stopped", retry: false));
        await Within(attachments.SweepAsync());
        await Assert.That(server.AttachedRejections).IsEquivalentTo([(dispatch, agentId, "attachment_ended")]);
    }

    /// <summary>A <c>/resume</c> back to the session the process left names the attachment it is leaving, so it carries
    /// on rather than haunts; with nothing named, or another agent named, it is still the ghost the refusal is for.</summary>
    [Test]
    public async Task A_process_returning_to_a_session_it_left_can_attach_to_it_again() {
        var server = new CaptureServerConnection();
        await using var attachments = Attachments(server);
        var first  = await AttachAsync(attachments);
        var second = await AttachAsync(attachments, session: Later, replaces: first);

        await Assert.That(await attachments.AttachAsync(Session, Key, null, CancellationToken.None))
            .IsEqualTo(SteeringAttachReply.Refusal("replaced", retry: false));
        await Assert.That(await attachments.AttachAsync(Session, Key, AgentOf("c56a4180-65aa-42ec-a945-5fd21dec0538"), CancellationToken.None))
            .IsEqualTo(SteeringAttachReply.Refusal("replaced", retry: false));

        await Assert.That(await AttachAsync(attachments, replaces: second)).IsEqualTo(first);
        await Assert.That(attachments.Holds(second)).IsFalse();
        await Assert.That(await attachments.AttachAsync(Later, Key, null, CancellationToken.None))
            .IsEqualTo(SteeringAttachReply.Refusal("replaced", retry: false));
    }

    /// <summary>A process carrying on back to the session it left is no ghost of it, so the server finding that session
    /// still attached is "not now", as it is for a first attach.</summary>
    [Test]
    public async Task A_process_carrying_on_back_is_told_to_attach_again_while_its_session_is_still_attached() {
        var server = new CaptureServerConnection();
        await using var attachments = Attachments(server);
        var first  = await AttachAsync(attachments);
        var second = await AttachAsync(attachments, session: Later, replaces: first);
        server.AttachedRegistrationAnswer = r => r.AgentId == first ? new AgentRegisteredResponse(false, "session_attached") : null;

        await Assert.That(await attachments.AttachAsync(Session, Key, second, CancellationToken.None))
            .IsEqualTo(SteeringAttachReply.Refusal("session_attached", retry: true));
        await Assert.That(attachments.Holds(second)).IsTrue();
    }

    /// <summary>Another process attaching as an agent this one carried on from is the ghost the refusal is for, whether
    /// it names nothing or an attachment that is not its own: the server finding the session attached is final.</summary>
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task A_ghost_the_server_finds_attached_is_told_not_to_attach_again(bool namesTheReplacement) {
        var server = new CaptureServerConnection();
        await using var attachments = Attachments(server);
        var first  = await AttachAsync(attachments);
        var second = await AttachAsync(attachments, session: Later, replaces: first);
        server.AttachedRegistrationAnswer = r => r.AgentId == first ? new AgentRegisteredResponse(false, "session_attached") : null;

        await Assert.That(await attachments.AttachAsync(Session, OtherKey, namesTheReplacement ? second : null, CancellationToken.None))
            .IsEqualTo(SteeringAttachReply.Refusal("session_attached", retry: false));
    }

    /// <summary>A report the server answers with an error is sent again by the next sweep, not by every flush, and holds
    /// back only what is reported about the same dispatch: the link behind it about another dispatch goes at once.</summary>
    [Test]
    public async Task A_report_the_server_refuses_does_not_hold_back_the_ones_behind_it() {
        var server = new CaptureServerConnection();
        await using var attachments = Attachments(server);
        var agentId  = await AttachAsync(attachments);
        var refused  = Guid.NewGuid();
        var landed   = Guid.NewGuid();
        var row      = Guid.NewGuid();
        var lateRow  = Guid.NewGuid();

        server.Ready = false;
        attachments.Undelivered(agentId, Key, refused, rejected: true, "stopped");
        attachments.Link(agentId, Key, refused, Session, lateRow);
        attachments.Link(agentId, Key, landed, Session, row);
        server.Ready = true;
        server.AttachedRejectionThrow = new Microsoft.AspNetCore.SignalR.HubException("Caller is not the daemon owning agent");
        await Within(attachments.FlushReportsAsync());
        await Within(attachments.FlushReportsAsync());

        await Assert.That(server.LinkReports).IsEquivalentTo([new ReportInputLinkRequest(landed, agentId, Session, row)]);
        await Assert.That(server.AttachedRejectionAttempts).IsEqualTo(1);

        await Within(attachments.SweepAsync());

        await Assert.That(server.AttachedRejectionAttempts).IsEqualTo(2);
        await Assert.That(server.LinkReports.Count).IsEqualTo(1);

        server.AttachedRejectionThrow = null;
        await Within(attachments.SweepAsync());

        await Assert.That(server.AttachedRejections).IsEquivalentTo([(refused, agentId, "stopped")]);
        await Assert.That(server.LinkReports).IsEquivalentTo(
            [new ReportInputLinkRequest(landed, agentId, Session, row), new ReportInputLinkRequest(refused, agentId, Session, lateRow)],
            TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    /// <summary>A report the server keeps refusing is dropped once refused <see cref="ModReportQueue.MaxRefusals"/>
    /// times, counted across a daemon restart, and what was queued behind it about the same dispatch goes then.</summary>
    [Test]
    public async Task A_report_the_server_keeps_refusing_is_dropped_at_its_bound_counted_across_a_restart() {
        var refused = Guid.NewGuid();
        var row     = Guid.NewGuid();
        var error   = new Microsoft.AspNetCore.SignalR.HubException("Caller is not the daemon owning agent");
        var before  = new CaptureServerConnection { AttachedRejectionThrow = error };

        await using (var attachments = Attachments(before)) {
            var agentId = await AttachAsync(attachments);
            attachments.Undelivered(agentId, Key, refused, rejected: true, "stopped");
            attachments.Link(agentId, Key, refused, Session, row);
            await Within(attachments.SweepAsync());
        }

        await Assert.That(before.AttachedRejectionAttempts).IsEqualTo(2);
        await Assert.That(before.LinkReports).IsEmpty();

        var after = new CaptureServerConnection { AttachedRejectionThrow = error };
        await using var restarted = Attachments(after);

        for (var sweep = 0; sweep < ModReportQueue.MaxRefusals; sweep++) await Within(restarted.SweepAsync());

        await Assert.That(after.AttachedRejectionAttempts).IsEqualTo(ModReportQueue.MaxRefusals - 2);
        await Assert.That(after.LinkReports).IsEquivalentTo([new ReportInputLinkRequest(refused, AgentOf(Session), Session, row)]);
    }

    /// <summary>A rejection the server takes and records on no stream names an agent that was not sent the dispatch, which
    /// sending it again does not change: it goes at once, and no sweep sends it again.</summary>
    [Test]
    public async Task A_rejection_the_server_records_nowhere_is_not_sent_again() {
        var server = new CaptureServerConnection { AttachedRejectionRecorded = (_, _) => false };
        await using var attachments = Attachments(server);
        var agentId = await AttachAsync(attachments);

        attachments.Undelivered(agentId, Key, Guid.NewGuid(), rejected: true, "stopped");
        await Within(attachments.SweepAsync());

        await Assert.That(server.AttachedRejectionAttempts).IsEqualTo(1);
    }

    /// <summary>A report keeps an attachment alive only when it comes from the attached process: two processes can share
    /// the agent id of a session.</summary>
    [Test]
    public async Task A_report_from_another_process_keeps_nothing_alive() {
        var server = new CaptureServerConnection();
        await using var attachments = Attachments(server);
        var theirs = await AttachAsync(attachments);
        var ours   = await AttachAsync(attachments, session: Later);

        _time.Advance(ModAttachments.LapseBound - ModAttachments.SweepInterval);
        attachments.Link(theirs, OtherKey, Guid.NewGuid(), Session, Guid.NewGuid());
        attachments.Undelivered(ours, Key, Guid.NewGuid(), rejected: false, "no_row");
        _time.Advance(ModAttachments.SweepInterval * 2);
        await Within(attachments.SweepAsync());

        await Assert.That(attachments.Holds(theirs)).IsFalse();
        await Assert.That(attachments.Holds(ours)).IsTrue();
    }

    /// <summary>With no record here of where the lost dispatch went, every agent the process carried on from is tried, as
    /// the server follows them for a link.</summary>
    [Test]
    public async Task A_lost_dispatch_is_looked_for_along_every_agent_its_process_carried_on_from() {
        var server = new CaptureServerConnection();
        await using var attachments = Attachments(server);
        var first   = await AttachAsync(attachments);
        var second  = await AttachAsync(attachments, session: Later, replaces: first);
        var current = await AttachAsync(attachments, session: "c56a4180-65aa-42ec-a945-5fd21dec0538", replaces: second);
        var lost    = Guid.NewGuid();
        Push(attachments, current, Guid.NewGuid());
        server.HandoverAnswer            = _ => new ConfirmInputHandoverResponse(InputHandoverOutcome.Hold, lost);
        server.AttachedRejectionRecorded = (_, agentId) => agentId == first;

        await IdleAsync(attachments, current);

        await Assert.That(server.AttachedRejections.Select(r => r.AgentId)).IsEquivalentTo([current, second, first]);
    }

    /// <summary>The mod gives up on a call after twelve seconds, and a handover the server gave after that would reach a
    /// mod that has gone, so one poll stops asking once its own bound is spent, however many dispatches are held.</summary>
    [Test]
    public async Task A_poll_stops_asking_the_server_once_its_bound_is_spent() {
        var server = new CaptureServerConnection();
        await using var attachments = Attachments(server);
        var agentId = await AttachAsync(attachments);
        var settled = Guid.NewGuid();
        Push(attachments, agentId, settled, "settled already");
        Push(attachments, agentId, Guid.NewGuid(), "still wanted");
        server.HandoverAnswer = _ => {
            _time.Advance(ModAttachments.NextBudget + TimeSpan.FromSeconds(1));

            return new ConfirmInputHandoverResponse(InputHandoverOutcome.Drop);
        };

        await Assert.That(await IdleAsync(attachments, agentId)).IsNull();
        await Assert.That(server.HandoverRequests.Select(r => r.DispatchId)).IsEquivalentTo([settled]);
    }

    /// <summary>A server that can't be reached answers no agent of the walk, and the next poll asks again.</summary>
    [Test]
    public async Task A_lost_dispatch_walk_stops_at_a_server_that_does_not_answer() {
        var server = new CaptureServerConnection();
        await using var attachments = Attachments(server);
        var first   = await AttachAsync(attachments);
        var current = await AttachAsync(attachments, session: Later, replaces: first);
        Push(attachments, current, Guid.NewGuid());
        server.HandoverAnswer      = _ => new ConfirmInputHandoverResponse(InputHandoverOutcome.Hold, Guid.NewGuid());
        server.AttachedReportThrow = new IOException("connection reset");

        await IdleAsync(attachments, current);

        await Assert.That(server.AttachedRejectionAttempts).IsEqualTo(1);
    }

    /// <summary>The mod records a dispatch before it submits it, so one it reported on isn't lost, even once that report
    /// has been dropped at its refusal bound.</summary>
    [Test]
    public async Task A_dispatch_the_mod_reported_is_never_rejected_as_lost() {
        var error  = new HubException("Caller is not the daemon owning agent");
        var server = new CaptureServerConnection { AttachedRejectionThrow = error };
        await using var attachments = Attachments(server);
        var agentId  = await AttachAsync(attachments);
        var reported = Guid.NewGuid();
        Push(attachments, agentId, reported);
        await IdleAsync(attachments, agentId);

        attachments.Undelivered(agentId, Key, reported, rejected: true, "stopped");
        for (var sweep = 0; sweep < ModReportQueue.MaxRefusals; sweep++) await Within(attachments.SweepAsync());
        var refusals = server.AttachedRejectionAttempts;

        server.AttachedRejectionThrow = null;
        Push(attachments, agentId, Guid.NewGuid());
        server.HandoverAnswer = _ => new ConfirmInputHandoverResponse(InputHandoverOutcome.Hold, reported);

        await Assert.That(await IdleAsync(attachments, agentId)).IsNull();
        await Assert.That(server.AttachedRejectionAttempts).IsEqualTo(refusals);
        await Assert.That(server.AttachedRejections).IsEmpty();
    }

    /// <summary>A flood of reports from the owner's own processes fills the queue, and what it pushes out must not be the
    /// report of a dispatch the mod still has: without it, that dispatch would read as lost.</summary>
    [Test]
    public async Task A_full_report_queue_keeps_the_report_of_a_dispatch_still_handed_over() {
        var handed = Guid.NewGuid();
        var queue  = new ModReportQueue(new CaptureServerConnection { Ready = false }, NullLogger.Instance) { Keeps = id => id == handed };
        var agent  = AgentOf(Session);

        queue.Enqueue(new ModInputReport(ModInputReport.Link, handed, agent, Session, Guid.NewGuid()));
        for (var i = 0; i < 1024; i++) queue.Enqueue(new ModInputReport(ModInputReport.Link, Guid.NewGuid(), agent, Session, Guid.NewGuid()));

        await Assert.That(queue.Holds(handed)).IsTrue();
        await Assert.That(queue.Snapshot().Length).IsEqualTo(1024);
    }

    /// <summary>A link back is kept only for an agent still live or carried on from, so the links are bounded as the
    /// agents carried on from are, by processes that end without a detach too.</summary>
    [Test]
    public async Task The_links_back_go_with_the_agents_carried_on_from() {
        var server = new CaptureServerConnection();
        await using var attachments = Attachments(server);

        for (var i = 0; i <= 256; i++) {
            var key   = $"{5000 + i}:start";
            var first = await AttachAsync(attachments, session: Guid.NewGuid().ToString(), key: key);

            attachments.Stop(await AttachAsync(attachments, session: Guid.NewGuid().ToString(), key: key, replaces: first));
        }

        await Assert.That(new ModAttachmentStore(StatePath, NullLogger.Instance).Load().CarriedFrom!.Count).IsEqualTo(1);
    }
}
